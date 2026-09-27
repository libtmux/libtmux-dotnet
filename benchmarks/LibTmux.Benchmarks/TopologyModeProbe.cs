using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibTmux.Testing;

namespace LibTmux.Benchmarks;

/// <summary>Records interleaved five-mode workloads over linked-window topologies.</summary>
[UnsupportedOSPlatform("windows")]
internal static class TopologyModeProbe
{
    private const int Items = 8;
    private const int Concurrency = 4;
    private const int AttachedControlClients = 1;
    private const string QueryFormat = "Q|#{pane_id}";
    private static readonly string[] Modes =
    [
        "serial-process", "concurrent-process", "process-chain", "serial-control", "concurrent-control",
    ];
    private static readonly Topology[] Topologies =
    [
        new("small", 1, 1), new("medium", 4, 8), new("large", 16, 32),
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan SampleBudget = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync(
        string outputPath,
        bool smoke = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The topology probe requires tmux on Unix.");
        }

        int warmupRounds = smoke ? 0 : 5;
        int measuredRounds = smoke ? 1 : 20;
        string tmuxBinary = Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
            ?? throw new InvalidOperationException("Set LIBTMUX_TMUX to the benchmark tmux binary.");
        if (!Path.IsPathFullyQualified(tmuxBinary) || !File.Exists(tmuxBinary))
        {
            throw new InvalidOperationException("LIBTMUX_TMUX must name an existing absolute binary path.");
        }

        using FileStream binary = File.OpenRead(tmuxBinary);
        string tmuxBinarySha256 = Convert.ToHexString(SHA256.HashData(binary))
            .ToLowerInvariant();
        string path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using StreamWriter writer = new(file, new UTF8Encoding(false));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RunBudget);

        try
        {
            await RunCoreAsync(writer, warmupRounds, measuredRounds, tmuxBinary,
                tmuxBinarySha256, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The topology probe exceeded its eight-minute deadline.", error);
        }
    }

    private static async Task RunCoreAsync(
        StreamWriter writer,
        int warmupRounds,
        int measuredRounds,
        string tmuxBinary,
        string tmuxBinarySha256,
        CancellationToken cancellationToken)
    {
        (string sourceCommit, bool sourceClean) = await BenchmarkSourceProvenance.ReadAsync(
            cancellationToken).ConfigureAwait(false);
        int samples = 0;
        bool first = true;
        foreach (Topology topology in Topologies)
        {
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            setup.CancelAfter(TimeSpan.FromSeconds(45));
            Stopwatch setupClock = Stopwatch.StartNew();
            await using Fixture fixture = await Fixture.CreateAsync(topology, tmuxBinary, setup.Token)
                .ConfigureAwait(false);
            setupClock.Stop();

            if (first)
            {
                await WriteAsync(writer, new
                {
                    kind = "run",
                    collectedUtc = DateTimeOffset.UtcNow,
                    sourceCommit,
                    sourceClean,
                    tmuxBinaryPath = tmuxBinary,
                    tmuxBinarySha256,
                    tmuxVersion = fixture.Server.Version?.ToString(),
                    runtime = RuntimeInformation.FrameworkDescription,
                    os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    processors = Environment.ProcessorCount,
                    cpuModel = ReadCpuModel(),
                    settings = new
                    {
                        warmupRounds,
                        measuredRounds,
                        itemsPerSample = Items,
                        concurrencyCap = Concurrency,
                        runBudget = RunBudget,
                        sampleBudget = SampleBudget,
                    },
                }).ConfigureAwait(false);
                first = false;
            }

            await WriteAsync(writer, new
            {
                kind = "topology",
                topology = topology.Name,
                physicalWindows = fixture.PhysicalWindows,
                placements = fixture.Placements,
                queryRows = fixture.QueryRows,
                distinctPaneIds = fixture.DistinctPaneIds,
                attachedControlClients = AttachedControlClients,
                setupMs = setupClock.Elapsed.TotalMilliseconds,
            }).ConfigureAwait(false);

            for (int round = 0; round < warmupRounds + measuredRounds; round++)
            {
                for (int slot = 0; slot < Modes.Length; slot++)
                {
                    string mode = Modes[(round + slot) % Modes.Length];
                    bool warmup = round < warmupRounds;
                    using var sample = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    sample.CancelAfter(SampleBudget);
                    try
                    {
                        SampleResult result = await fixture.RunSampleAsync(
                            mode, round, slot, sample.Token).ConfigureAwait(false);
                        await WriteAsync(writer, new
                        {
                            kind = "sample",
                            topology = topology.Name,
                            physicalWindows = fixture.PhysicalWindows,
                            placements = fixture.Placements,
                            round,
                            slot,
                            mode,
                            warmup,
                            status = "ok",
                            validated = true,
                            result.ElapsedMs,
                            result.Processes,
                            attachedControlClients = AttachedControlClients,
                            usedControlClients = IsControlMode(mode) ? 1 : 0,
                            logicalCommands = Items * 3,
                            result.MaximumInFlight,
                        }).ConfigureAwait(false);
                        samples++;
                    }
                    catch (Exception error)
                    {
                        await WriteAsync(writer, new
                        {
                            kind = "sample",
                            topology = topology.Name,
                            round,
                            slot,
                            mode,
                            warmup,
                            status = "failed",
                            errorType = error.GetType().Name,
                            error.Message,
                        }).ConfigureAwait(false);
                        throw;
                    }
                }
            }

            await fixture.AssertControlClientAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteAsync(writer, new
        {
            kind = "summary",
            status = "ok",
            sampleCount = samples,
        }).ConfigureAwait(false);
    }

    private static Task WriteAsync<T>(StreamWriter writer, T value)
    {
        return WriteCoreAsync(writer, value);

        static async Task WriteCoreAsync(StreamWriter target, T item)
        {
            await target.WriteLineAsync(JsonSerializer.Serialize(item, JsonOptions)).ConfigureAwait(false);
            await target.FlushAsync().ConfigureAwait(false);
        }
    }

    private static bool IsControlMode(string mode) => mode is "serial-control" or "concurrent-control";

    private static string ReadCpuModel()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "unavailable";
        }

        try
        {
            string? line = File.ReadLines("/proc/cpuinfo")
                .FirstOrDefault(value => value.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            int separator = line?.IndexOf(':') ?? -1;
            if (separator >= 0)
            {
                string model = line![(separator + 1)..].Trim();
                if (model.Length > 0)
                {
                    return model;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return "unavailable";
    }

    private sealed record Topology(string Name, int PhysicalWindows, int Placements);

    private sealed record SampleResult(double ElapsedMs, int Processes, int MaximumInFlight);

    private sealed record ItemCommands(
        string Marker,
        TmuxCommand MarkerCommand,
        TmuxCommand CaptureCommand,
        TmuxCommand QueryCommand);

    private sealed record ItemResult(
        string Marker,
        IReadOnlyList<string> Capture,
        IReadOnlyList<string> Query);

    private sealed class ProcessCounter
    {
        internal int Enabled;
        internal int Count;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryHierarchyScope _scope;
        private readonly IControlModeSession _control;
        private readonly ProcessCounter _counter;
        private readonly TmuxCommand _captureCommand;
        private readonly TmuxCommand _queryCommand;
        private readonly byte[] _captureBytes;
        private readonly byte[] _queryBytes;
        private readonly string[] _captureLines;
        private readonly string[] _queryLines;
        private readonly string _nonce;

        private Fixture(
            TemporaryHierarchyScope scope,
            IControlModeSession control,
            ProcessCounter counter,
            TmuxCommand captureCommand,
            TmuxCommand queryCommand,
            TmuxCommandResult capture,
            TmuxCommandResult query,
            int physicalWindows,
            int placements,
            int distinctPaneIds)
        {
            _scope = scope;
            _control = control;
            _counter = counter;
            _captureCommand = captureCommand;
            _queryCommand = queryCommand;
            _captureBytes = capture.StandardOutput.ToArray();
            _queryBytes = query.StandardOutput.ToArray();
            _captureLines = CanonicalLines(capture.StandardOutputLines);
            _queryLines = CanonicalLines(query.StandardOutputLines);
            _nonce = Guid.NewGuid().ToString("N")[..8];
            PhysicalWindows = physicalWindows;
            Placements = placements;
            QueryRows = _queryLines.Length;
            DistinctPaneIds = distinctPaneIds;
        }

        internal Server Server => _scope.Server;

        internal int PhysicalWindows { get; }

        internal int Placements { get; }

        internal int QueryRows { get; }

        internal int DistinctPaneIds { get; }

        internal static async Task<Fixture> CreateAsync(
            Topology topology,
            string tmuxBinary,
            CancellationToken cancellationToken)
        {
            var counter = new ProcessCounter();
            var options = new TmuxTestOptions(new ServerConnectionOptions
            {
                TmuxBinaryPath = tmuxBinary,
                SocketName = $"lt-topology-{Guid.NewGuid():N}"[..24],
                ConfigurationFile = "/dev/null",
                CommandTimeout = SampleBudget,
                ChildEnvironment = new Dictionary<string, string?>
                {
                    ["TMUX"] = null,
                    ["TMUX_PANE"] = null,
                },
                Interceptor = (_, next, token) =>
                {
                    if (Volatile.Read(ref counter.Enabled) != 0)
                    {
                        Interlocked.Increment(ref counter.Count);
                    }

                    return next(token);
                },
            });

            TemporaryHierarchyScope scope = await new TmuxTestFactory().CreateHierarchyAsync(
                options, cancellationToken).ConfigureAwait(false);
            IControlModeSession? control = null;
            try
            {
                var windows = new List<Window> { scope.Window };
                for (int index = 1; index < topology.PhysicalWindows; index++)
                {
                    windows.Add(await scope.Session.CreateWindowAsync(
                        new NewWindowRequest { Name = $"topology-{index}" }, cancellationToken)
                        .ConfigureAwait(false));
                }

                int extraPlacements = topology.Placements - topology.PhysicalWindows;
                for (int index = 0; index < extraPlacements; index++)
                {
                    await windows[index].LinkAsync(new LinkWindowRequest(scope.Session.Id.ToString())
                    {
                        TargetIndex = (100 + index).ToString(CultureInfo.InvariantCulture),
                        Detach = true,
                    }, cancellationToken).ConfigureAwait(false);
                }

                IReadOnlyList<Window> placements = await scope.Session.GetWindowsAsync(
                    cancellationToken).ConfigureAwait(false);
                int physical = placements.Select(window => window.Id).Distinct().Count();
                if (physical != topology.PhysicalWindows || placements.Count != topology.Placements)
                {
                    throw new InvalidOperationException(
                        $"Topology '{topology.Name}' has {physical} physical windows and "
                        + $"{placements.Count} placements; expected "
                        + $"{topology.PhysicalWindows} and {topology.Placements}.");
                }

                control = await scope.Server.EnterControlModeAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await scope.Pane.RespawnAsync(new RespawnRequest
                {
                    Command = "cat",
                    KillExistingProcess = true,
                }, cancellationToken).ConfigureAwait(false);
                string fixtureToken = $"fixture{Guid.NewGuid():N}"[..19];
                await SeedPaneAsync(scope.Pane, control, fixtureToken, cancellationToken)
                    .ConfigureAwait(false);

                TmuxCommand captureCommand = new CapturePaneRequest
                {
                    JoinWrappedLines = true,
                }.ToCommand(scope.Pane);
                TmuxCommand queryCommand = TmuxCommand.Create("list-panes", "-a", "-F", QueryFormat);
                TmuxCommandResult capture = await scope.Server.Chain().Then(captureCommand)
                    .ExecuteAsync(cancellationToken).ConfigureAwait(false);
                TmuxCommandResult query = await scope.Server.Chain().Then(queryCommand)
                    .ExecuteAsync(cancellationToken).ConfigureAwait(false);
                string[] captureLines = CanonicalLines(capture.StandardOutputLines);
                string[] queryLines = CanonicalLines(query.StandardOutputLines);
                if (!captureLines.Any(line => line.Contains(fixtureToken, StringComparison.Ordinal))
                    || captureLines.Any(line => line.StartsWith("M|", StringComparison.Ordinal)
                        || line.StartsWith("Q|", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("The topology capture lacks its fixture marker or has reserved output.");
                }

                if (queryLines.Length != placements.Count
                    || queryLines.Any(line => !line.StartsWith("Q|%", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        "The topology query did not return one pane ID row per placement.");
                }

                int distinctPanes = queryLines.Select(line => line[2..])
                    .Distinct(StringComparer.Ordinal).Count();
                if (distinctPanes != physical)
                {
                    throw new InvalidOperationException(
                        $"Topology '{topology.Name}' query has {distinctPanes} physical pane IDs; expected {physical}.");
                }

                var fixture = new Fixture(scope, control, counter, captureCommand, queryCommand,
                    capture, query, physical, placements.Count, distinctPanes);
                await fixture.AssertControlClientAsync(cancellationToken).ConfigureAwait(false);
                return fixture;
            }
            catch
            {
                try
                {
                    if (control is not null)
                    {
                        await control.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    await scope.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }

        internal async Task AssertControlClientAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<Client> clients = await Server.GetClientsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (clients.Count != 1 || !clients[0].IsControlClient)
            {
                throw new InvalidOperationException(
                    $"The topology fixture has {clients.Count} clients; expected one control client.");
            }
        }

        internal async Task<SampleResult> RunSampleAsync(
            string mode,
            int round,
            int slot,
            CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _counter.Count, 0);
            Volatile.Write(ref _counter.Enabled, 1);
            Stopwatch clock = Stopwatch.StartNew();
            int maximum;
            try
            {
                ItemCommands[] input = BuildInput(round, slot);
                (ItemResult[] results, maximum) = await RunModeAsync(mode, input, cancellationToken)
                    .ConfigureAwait(false);
                AssertResults(mode, input, results);
            }
            finally
            {
                clock.Stop();
                Volatile.Write(ref _counter.Enabled, 0);
            }

            int processes = Volatile.Read(ref _counter.Count);
            int expected = mode switch
            {
                "serial-process" or "concurrent-process" => Items * 3,
                "process-chain" => 1,
                "serial-control" or "concurrent-control" => 0,
                _ => throw new InvalidOperationException($"Unknown topology mode '{mode}'."),
            };
            if (processes != expected)
            {
                throw new InvalidOperationException(
                    $"Mode '{mode}' started {processes} tmux processes; expected {expected}.");
            }

            if ((mode is "concurrent-process" or "concurrent-control") && maximum < 2)
            {
                throw new InvalidOperationException(
                    $"Mode '{mode}' did not overlap work despite a concurrency cap of {Concurrency}.");
            }

            return new SampleResult(clock.Elapsed.TotalMilliseconds, processes, maximum);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _control.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _scope.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static async Task SeedPaneAsync(
            Pane pane,
            IControlModeSession control,
            string token,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using IAsyncEnumerator<TmuxEvent> events = control.Events.GetAsyncEnumerator(timeout.Token);
            Task<bool> next = events.MoveNextAsync().AsTask();
            await pane.SendTextAsync(token, enter: false, timeout.Token).ConfigureAwait(false);
            var seen = new StringBuilder();
            while (await next.ConfigureAwait(false))
            {
                if (events.Current is TmuxOutputEvent output && output.PaneId == pane.Id)
                {
                    seen.Append(output.Data);
                    if (seen.ToString().Contains(token, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                next = events.MoveNextAsync().AsTask();
            }

            throw new InvalidOperationException("The topology pane stopped producing output before its fixture marker.");
        }

        private ItemCommands[] BuildInput(int round, int slot) =>
        [
            .. Enumerable.Range(0, Items).Select(index =>
            {
                string marker = $"M|{_nonce}|{round}|{slot}|{index}";
                return new ItemCommands(
                    marker,
                    new DisplayMessageRequest { Message = marker, ReturnText = true }.ToCommand(Server),
                    _captureCommand,
                    _queryCommand);
            }),
        ];

        private Task<(ItemResult[] Results, int Maximum)> RunModeAsync(
            string mode,
            ItemCommands[] input,
            CancellationToken cancellationToken) => mode switch
            {
                "serial-process" => RunSerialAsync(input, RunProcessItemAsync, cancellationToken),
                "concurrent-process" => RunConcurrentAsync(input, RunProcessItemAsync, cancellationToken),
                "process-chain" => RunChainAsync(input, cancellationToken),
                "serial-control" => RunSerialAsync(input, RunControlItemAsync, cancellationToken),
                "concurrent-control" => RunConcurrentAsync(input, RunControlItemAsync, cancellationToken),
                _ => throw new InvalidOperationException($"Unknown topology mode '{mode}'."),
            };

        private static async Task<(ItemResult[], int)> RunSerialAsync(
            ItemCommands[] input,
            Func<ItemCommands, CancellationToken, Task<ItemResult>> run,
            CancellationToken cancellationToken)
        {
            ItemResult[] results = new ItemResult[input.Length];
            for (int index = 0; index < input.Length; index++)
            {
                results[index] = await run(input[index], cancellationToken).ConfigureAwait(false);
            }

            return (results, 1);
        }

        private static async Task<(ItemResult[], int)> RunConcurrentAsync(
            ItemCommands[] input,
            Func<ItemCommands, CancellationToken, Task<ItemResult>> run,
            CancellationToken cancellationToken)
        {
            using var slots = new SemaphoreSlim(Concurrency);
            int active = 0;
            int maximum = 0;
            Task<ItemResult>[] tasks = [.. input.Select(async item =>
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int current = Interlocked.Increment(ref active);
                    InterlockedExtensions.Max(ref maximum, current);
                    return await run(item, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                    slots.Release();
                }
            })];
            return (await Task.WhenAll(tasks).ConfigureAwait(false), maximum);
        }

        private async Task<ItemResult> RunProcessItemAsync(
            ItemCommands item,
            CancellationToken cancellationToken)
        {
            TmuxCommandResult marker = await Server.Chain().Then(item.MarkerCommand)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            TmuxCommandResult capture = await Server.Chain().Then(item.CaptureCommand)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            TmuxCommandResult query = await Server.Chain().Then(item.QueryCommand)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return ReadItem(marker.StandardOutputLines, capture.StandardOutputLines, query.StandardOutputLines);
        }

        private async Task<ItemResult> RunControlItemAsync(
            ItemCommands item,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<string> marker = await _control.SendAsync(item.MarkerCommand, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<string> capture = await _control.SendAsync(item.CaptureCommand, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<string> query = await _control.SendAsync(item.QueryCommand, cancellationToken)
                .ConfigureAwait(false);
            return ReadItem(marker, capture, query);
        }

        private async Task<(ItemResult[], int)> RunChainAsync(
            ItemCommands[] input,
            CancellationToken cancellationToken)
        {
            TmuxChain chain = Server.Chain();
            foreach (ItemCommands item in input)
            {
                chain = chain.Then(item.MarkerCommand).Then(item.CaptureCommand).Then(item.QueryCommand);
            }

            TmuxCommandResult output = await chain.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return (ReadChain(input, output.StandardOutput.ToArray()), 1);
        }

        private ItemResult[] ReadChain(ItemCommands[] input, byte[] output)
        {
            ItemResult[] results = new ItemResult[input.Length];
            int offset = 0;
            for (int index = 0; index < input.Length; index++)
            {
                ReadSegment(output, ref offset, Encoding.UTF8.GetBytes(input[index].Marker + "\n"), index, "marker");
                ReadSegment(output, ref offset, _captureBytes, index, "capture");
                ReadSegment(output, ref offset, _queryBytes, index, "query");
                results[index] = new ItemResult(input[index].Marker, _captureLines, _queryLines);
            }

            if (offset != output.Length)
            {
                throw new InvalidOperationException(
                    $"The topology chain returned {output.Length - offset} unattributed output bytes.");
            }

            return results;
        }

        private static void ReadSegment(byte[] output, ref int offset, byte[] expected, int index, string kind)
        {
            if (expected.Length > output.Length - offset
                || !output.AsSpan(offset, expected.Length).SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"The topology chain's {kind} output for input {index} differs at byte {offset}.");
            }

            offset += expected.Length;
        }

        private static ItemResult ReadItem(
            IReadOnlyList<string> marker,
            IReadOnlyList<string> capture,
            IReadOnlyList<string> query)
        {
            if (marker.Count != 1)
            {
                throw new InvalidOperationException($"The topology item returned {marker.Count} marker lines.");
            }

            return new ItemResult(marker[0], CanonicalLines(capture), CanonicalLines(query));
        }

        private static string[] CanonicalLines(IReadOnlyList<string> lines)
        {
            int count = lines.Count;
            while (count > 0 && lines[count - 1].Length == 0)
            {
                count--;
            }

            return [.. lines.Take(count)];
        }

        private void AssertResults(string mode, ItemCommands[] input, ItemResult[] results)
        {
            if (results.Length != Items)
            {
                throw new InvalidOperationException(
                    $"Mode '{mode}' returned {results.Length} items; expected {Items}.");
            }

            for (int index = 0; index < Items; index++)
            {
                ItemResult actual = results[index];
                if (!string.Equals(actual.Marker, input[index].Marker, StringComparison.Ordinal)
                    || !actual.Capture.SequenceEqual(_captureLines, StringComparer.Ordinal)
                    || !actual.Query.SequenceEqual(_queryLines, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Mode '{mode}' returned different marker, capture, or query data for input {index}.");
                }
            }
        }
    }

    private static class InterlockedExtensions
    {
        internal static void Max(ref int target, int value)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref target);
                if (observed >= value)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, observed) != observed);
        }
    }
}
