using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using LibTmux.Testing;

namespace LibTmux.Benchmarks;

internal sealed record StreamingProbeOptions
{
    internal int SamplesPerPayload { get; init; } = 20;
    internal int WarmupsPerPayload { get; init; } = 2;
    internal int LatencyPayloadBytes { get; init; } = 64;
    internal int ThroughputPayloadBytes { get; init; } = 256 * 1024;
    internal int OverflowSamples { get; init; } = 5;
    internal int DisposalSamples { get; init; } = 5;
    internal int OverflowBufferCapacity { get; init; } = 8;
    internal int OverflowNotifications { get; init; } = 32;
    internal TimeSpan RunBudget { get; init; } = TimeSpan.FromMinutes(8);
    internal TimeSpan SampleBudget { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Records live pane-output delivery and control-client cleanup.</summary>
[UnsupportedOSPlatform("windows")]
internal static class StreamingProbe
{
    private const char StartMarker = '\u0002';
    private const char EndMarker = '\u0003';
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(
        string outputPath,
        StreamingProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The streaming probe requires tmux on Unix.");
        }

        options ??= new StreamingProbeOptions();
        Validate(options);
        string path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using StreamWriter writer = new(file, new UTF8Encoding(false));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RunBudget);

        try
        {
            await RunCoreAsync(writer, options, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The streaming probe exceeded its {options.RunBudget.TotalMinutes:g}-minute deadline.",
                error);
        }
    }

    private static async Task RunCoreAsync(
        StreamWriter writer,
        StreamingProbeOptions options,
        CancellationToken cancellationToken)
    {
        var factory = new TmuxTestFactory();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            TestOptions(eventBufferCapacity: null), cancellationToken).ConfigureAwait(false);
        (string sourceCommit, bool sourceClean) = await BenchmarkSourceProvenance.ReadAsync(
            cancellationToken).ConfigureAwait(false);
        await WriteAsync(writer, new
        {
            kind = "run",
            collectedUtc = DateTimeOffset.UtcNow,
            sourceCommit,
            sourceClean,
            tmuxVersion = scope.Server.Version?.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            settings = new
            {
                options.SamplesPerPayload,
                options.WarmupsPerPayload,
                options.LatencyPayloadBytes,
                options.ThroughputPayloadBytes,
                options.OverflowSamples,
                options.DisposalSamples,
                options.OverflowBufferCapacity,
                options.OverflowNotifications,
                options.RunBudget,
                options.SampleBudget,
            },
        }).ConfigureAwait(false);

        await using (IControlModeSession control = await scope.Server.EnterControlModeAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            await RunPayloadSamplesAsync(
                writer, scope.Pane, control, "latency", options.LatencyPayloadBytes,
                options, cancellationToken).ConfigureAwait(false);
            await RunPayloadSamplesAsync(
                writer, scope.Pane, control, "throughput", options.ThroughputPayloadBytes,
                options, cancellationToken).ConfigureAwait(false);
        }

        await using TemporaryHierarchyScope overflowScope = await factory.CreateHierarchyAsync(
            TestOptions(options.OverflowBufferCapacity), cancellationToken).ConfigureAwait(false);
        for (int index = 0; index < options.OverflowSamples; index++)
        {
            int sample = index;
            await RecordAsync(
                writer, "notification-buffer-overflow", sample, warmup: false,
                () => RunOverflowAsync(overflowScope, options, cancellationToken))
                .ConfigureAwait(false);
        }

        for (int index = 0; index < options.DisposalSamples; index++)
        {
            int sample = index;
            await RecordAsync(
                writer, "pending-reader-disposal", sample, warmup: false,
                () => RunDisposalAsync(scope.Server, options, cancellationToken))
                .ConfigureAwait(false);
        }
    }

    private static async Task RunPayloadSamplesAsync(
        StreamWriter writer,
        Pane pane,
        IControlModeSession control,
        string scenario,
        int payloadBytes,
        StreamingProbeOptions options,
        CancellationToken cancellationToken)
    {
        int count = options.WarmupsPerPayload + options.SamplesPerPayload;
        for (int index = 0; index < count; index++)
        {
            int sample = index;
            await RecordAsync(
                writer, scenario, sample, sample < options.WarmupsPerPayload,
                () => RunPayloadAsync(pane, control, payloadBytes, options, cancellationToken))
                .ConfigureAwait(false);
        }
    }

    private static async Task<object> RunPayloadAsync(
        Pane pane,
        IControlModeSession control,
        int payloadBytes,
        StreamingProbeOptions options,
        CancellationToken cancellationToken)
    {
        using var sample = SampleCancellation(options, cancellationToken);
        string command = $"printf '\\002'; printf '%{payloadBytes}s' '' | tr ' ' A; printf '\\003'";
        Task<PayloadReceipt> receiving = ReadPayloadAsync(control, pane.Id, payloadBytes, sample.Token);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long trigger = Stopwatch.GetTimestamp();
        try
        {
            await new SendKeysRequest { Text = command, Literal = true }
                .ExecuteAsync(pane, control, sample.Token).ConfigureAwait(false);
            PayloadReceipt receipt = await receiving.ConfigureAwait(false);
            long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
            double firstMs = Stopwatch.GetElapsedTime(trigger, receipt.FirstTick).TotalMilliseconds;
            double completeMs = Stopwatch.GetElapsedTime(trigger, receipt.EndTick).TotalMilliseconds;
            return new
            {
                payloadBytes,
                firstOutputMs = firstMs,
                completeOutputMs = completeMs,
                endToEndBytesPerSecond = payloadBytes / (completeMs / 1000.0),
                receipt.OutputEvents,
                processAllocatedBytes = allocatedAfter - allocatedBefore,
            };
        }
        catch (Exception failure)
        {
            await sample.CancelAsync().ConfigureAwait(false);
            try
            {
                await receiving.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sample.IsCancellationRequested)
            {
            }
            catch (Exception readerFailure)
            {
                failure.Data["LibTmux.StreamingProbeReaderFailure"] = readerFailure;
            }

            throw;
        }
    }

    private static async Task<PayloadReceipt> ReadPayloadAsync(
        IControlModeSession control,
        PaneId paneId,
        int expectedBytes,
        CancellationToken cancellationToken)
    {
        await using IAsyncEnumerator<TmuxEvent> reader = control.Events.GetAsyncEnumerator(
            cancellationToken);
        bool started = false;
        int received = 0;
        int outputEvents = 0;
        long firstTick = 0;
        while (await reader.MoveNextAsync().ConfigureAwait(false))
        {
            switch (reader.Current)
            {
                case TmuxEventsDroppedEvent loss:
                    throw new InvalidOperationException(
                        $"The loss-free pane stream dropped {loss.Count} events.");
                case TmuxOutputEvent output when output.PaneId == paneId:
                    outputEvents++;
                    foreach (char character in output.Data)
                    {
                        if (!started)
                        {
                            if (character == StartMarker)
                            {
                                started = true;
                                firstTick = Stopwatch.GetTimestamp();
                            }

                            continue;
                        }

                        if (character == EndMarker)
                        {
                            if (received != expectedBytes)
                            {
                                throw new InvalidOperationException(
                                    $"The pane stream delivered {received} of {expectedBytes} payload bytes.");
                            }

                            // The shell may print a prompt after the producer's end marker.
                            return new PayloadReceipt(firstTick, Stopwatch.GetTimestamp(), outputEvents);
                        }

                        if (character != 'A' || ++received > expectedBytes)
                        {
                            throw new InvalidOperationException(
                                $"The pane stream changed payload byte {received}.");
                        }
                    }

                    break;
                case TmuxExitEvent:
                    throw new InvalidOperationException(
                        "The control client exited before the pane payload ended.");
            }
        }

        throw new InvalidOperationException("The pane stream ended before the payload marker arrived.");
    }

    private static async Task<object> RunOverflowAsync(
        TemporaryHierarchyScope scope,
        StreamingProbeOptions options,
        CancellationToken cancellationToken)
    {
        using var sample = SampleCancellation(options, cancellationToken);
        await using IControlModeSession control = await scope.Server.EnterControlModeAsync(
            cancellationToken: sample.Token).ConfigureAwait(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long heapBefore = GC.GetTotalMemory(forceFullCollection: false);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long trigger = Stopwatch.GetTimestamp();
        await SendRenameBurstAsync(
            scope.Server, scope.Session.Id.ToString(), options.OverflowNotifications,
            sample.Token).ConfigureAwait(false);
        await RequireReplyAsync(control, "barrier", sample.Token).ConfigureAwait(false);
        long barrier = Stopwatch.GetTimestamp();
        long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        // The producer's command chain has returned before retained heap is read.
        long heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        await using IAsyncEnumerator<TmuxEvent> reader = control.Events.GetAsyncEnumerator(sample.Token);
        if (!await reader.MoveNextAsync().ConfigureAwait(false)
            || reader.Current is not TmuxEventsDroppedEvent loss
            || loss.Count <= 0)
        {
            throw new InvalidOperationException(
                "The real tmux notification burst did not report buffer overflow.");
        }

        await RequireReplyAsync(control, "alive", sample.Token).ConfigureAwait(false);
        if (!control.IsRunning)
        {
            throw new InvalidOperationException(
                "The control client stopped after the notification buffer overflowed.");
        }

        return new
        {
            producedRenameCommands = options.OverflowNotifications,
            bufferCapacityEvents = options.OverflowBufferCapacity,
            loss.Count,
            loss.TotalDropped,
            barrierMs = Stopwatch.GetElapsedTime(trigger, barrier).TotalMilliseconds,
            processAllocatedBytes = allocatedAfter - allocatedBefore,
            processHeapBeforeBytes = heapBefore,
            processHeapAfterBytes = heapAfter,
            controlStillRunning = control.IsRunning,
        };
    }

    private static async Task SendRenameBurstAsync(
        Server server,
        string sessionTarget,
        int notifications,
        CancellationToken cancellationToken)
    {
        TmuxChain burst = server.Chain();
        for (int index = 0; index < notifications; index++)
        {
            burst = burst.Then("rename-session", "-t", sessionTarget,
                index % 2 == 0 ? "stream-even" : "stream-odd");
        }

        await burst.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> RunDisposalAsync(
        Server server,
        StreamingProbeOptions options,
        CancellationToken cancellationToken)
    {
        using var sample = SampleCancellation(options, cancellationToken);
        await using IControlModeSession control = await server.EnterControlModeAsync(
            cancellationToken: sample.Token).ConfigureAwait(false);
        var pendingRead = new TaskCompletionSource<Task<bool>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> reading = ReadUntilExitAsync(control, pendingRead, sample.Token);
        Task first = await Task.WhenAny(pendingRead.Task, reading).WaitAsync(sample.Token)
            .ConfigureAwait(false);
        if (first == reading)
        {
            await reading.ConfigureAwait(false);
            throw new InvalidOperationException(
                "The control stream ended before the disposal reader could wait for an event.");
        }

        Task<bool> pending = await pendingRead.Task.WaitAsync(sample.Token).ConfigureAwait(false);
        if (pending.IsCompleted)
        {
            throw new InvalidOperationException(
                "The disposal reader was no longer pending when disposal began.");
        }

        long trigger = Stopwatch.GetTimestamp();
        await control.DisposeAsync().AsTask().WaitAsync(sample.Token).ConfigureAwait(false);
        long disposed = Stopwatch.GetTimestamp();
        bool sawExit = await reading.WaitAsync(sample.Token).ConfigureAwait(false);
        long readerDone = Stopwatch.GetTimestamp();
        if (!sawExit || control.IsRunning)
        {
            throw new InvalidOperationException(
                "Disposal did not complete the pending reader with a control exit event.");
        }

        IReadOnlyList<string>? reply = await server.DisplayMessageAsync(
            new DisplayMessageRequest { Message = "alive", ReturnText = true }, sample.Token)
            .ConfigureAwait(false);
        if (reply is null || reply.Count != 1 || reply[0] != "alive")
        {
            throw new InvalidOperationException(
                "The tmux server stopped when the control client was disposed.");
        }

        return new
        {
            disposalMs = Stopwatch.GetElapsedTime(trigger, disposed).TotalMilliseconds,
            readerCompletionMs = Stopwatch.GetElapsedTime(trigger, readerDone).TotalMilliseconds,
            sawExit,
            readerPendingAtTrigger = true,
            controlStillRunning = control.IsRunning,
            serverStillRunning = true,
        };
    }

    private static async Task<bool> ReadUntilExitAsync(
        IControlModeSession control,
        TaskCompletionSource<Task<bool>> pendingRead,
        CancellationToken cancellationToken)
    {
        await using IAsyncEnumerator<TmuxEvent> reader = control.Events.GetAsyncEnumerator(
            cancellationToken);
        while (true)
        {
            Task<bool> next = reader.MoveNextAsync().AsTask();
            if (!next.IsCompleted)
            {
                pendingRead.TrySetResult(next);
            }

            if (!await next.ConfigureAwait(false))
            {
                return false;
            }

            if (reader.Current is TmuxEventsDroppedEvent)
            {
                throw new InvalidOperationException("The disposal reader lost control events.");
            }

            if (reader.Current is TmuxExitEvent)
            {
                return true;
            }
        }
    }

    private static async Task RequireReplyAsync(
        IControlModeSession control,
        string message,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> reply = await control.SendAsync(
            TmuxCommand.Create("display-message", "-p", message), cancellationToken)
            .ConfigureAwait(false);
        if (reply.Count != 1 || reply[0] != message)
        {
            throw new InvalidOperationException(
                $"The control client did not answer the '{message}' command.");
        }
    }

    private static TmuxTestOptions TestOptions(int? eventBufferCapacity) =>
        new(new ServerConnectionOptions
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = $"ltstream-{Guid.NewGuid():N}"[..24],
            ConfigurationFile = "/dev/null",
            ControlModeEventBufferCapacity = eventBufferCapacity,
            ChildEnvironment = new Dictionary<string, string?>
            {
                ["TMUX"] = null,
                ["TMUX_PANE"] = null,
            },
        });

    private static CancellationTokenSource SampleCancellation(
        StreamingProbeOptions options,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(options.SampleBudget);
        return source;
    }

    private static async Task RecordAsync<T>(
        StreamWriter writer,
        string scenario,
        int sample,
        bool warmup,
        Func<Task<T>> run)
    {
        try
        {
            T measurement = await run().ConfigureAwait(false);
            await WriteAsync(writer, new
            {
                kind = "sample",
                scenario,
                sample,
                warmup,
                status = "ok",
                measurement,
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await WriteAsync(writer, new
            {
                kind = "sample",
                scenario,
                sample,
                warmup,
                status = "failed",
                errorType = error.GetType().Name,
                errorMessage = error.Message,
            }).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteAsync<T>(StreamWriter writer, T value)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions)).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static void Validate(StreamingProbeOptions options)
    {
        if (options.SamplesPerPayload <= 0 || options.WarmupsPerPayload < 0
            || options.LatencyPayloadBytes <= 0 || options.ThroughputPayloadBytes <= 0
            || options.OverflowSamples <= 0 || options.DisposalSamples <= 0
            || options.OverflowBufferCapacity <= 0
            || options.OverflowNotifications <= options.OverflowBufferCapacity
            || options.SampleBudget <= TimeSpan.Zero
            || options.RunBudget <= TimeSpan.Zero
            || options.RunBudget > TimeSpan.FromMinutes(9)
            || options.SampleBudget >= options.RunBudget)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Streaming probe counts and time budgets must be positive, and the run budget cannot exceed nine minutes.");
        }
    }

    private sealed record PayloadReceipt(long FirstTick, long EndTick, int OutputEvents);
}
