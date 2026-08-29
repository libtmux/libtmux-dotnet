using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>Reads one tmux control client and correlates what it says.</summary>
/// <remarks>
/// tmux answers on one stream that carries two different things: blocks that
/// answer a command, and notifications nobody asked for. A single reader owns
/// the stream and splits them, because two readers on one pipe would interleave
/// and neither would see a whole block.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed class ControlModeSession : IControlModeSession
{
    private readonly IControlModeProcess _process;
    private readonly ServerGeneration? _generation;
    private readonly TimeSpan _disposalBudget;
    private readonly ControlModeLimits _limits;
    private readonly Func<string> _sentinelFactory;
    private readonly TimeProvider _timeProvider;
    /// <summary>How many unread events are held before the oldest are dropped.</summary>
    /// <remarks>
    /// A pane can outpace any reader, and a caller may never read
    /// <see cref="Events"/> at all, so unbounded buffering has no ceiling.
    /// The buffer drops the oldest event instead of blocking, since blocking
    /// would also stall the reader that completes commands.
    /// </remarks>
    internal const int EventBufferCapacity = 512;

    private readonly ControlModeEventBuffer _events = new(EventBufferCapacity);

    private readonly Queue<PendingControlModeCommand> _pending = new();
    private readonly SemaphoreSlim _pendingSlots;
    private readonly SemaphoreSlim _writeLock;
    private readonly object _disposeGate = new();
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Task _pump;
    private Task? _disposeTask;
    private int _stopRequested;

    /// <summary>How long disposal awaits cleanup work.</summary>
    /// <remarks>
    /// Process state, close, kill, and dispose calls are synchronous. This
    /// bounds the waits they begin; it cannot preempt a synchronous process API.
    /// </remarks>
    private static readonly TimeSpan DefaultDisposalBudget = TimeSpan.FromSeconds(5);

    internal ControlModeSession(
        IControlModeProcess process,
        SemaphoreSlim? writeLock = null,
        TimeSpan? disposalBudget = null,
        ServerGeneration? generation = null,
        Func<string>? sentinelFactory = null,
        ControlModeLimits? limits = null,
        TimeProvider? timeProvider = null)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _generation = generation;
        _limits = limits ?? new ControlModeLimits();
        _pendingSlots = new SemaphoreSlim(
            _limits.MaxPendingCommands,
            _limits.MaxPendingCommands);
        _writeLock = writeLock ?? new SemaphoreSlim(1, 1);
        _disposalBudget = disposalBudget ?? DefaultDisposalBudget;
        _sentinelFactory = sentinelFactory ?? CreateSentinel;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_disposalBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(disposalBudget));
        }

        _pump = Task.Run(PumpAsync);
    }

    public IAsyncEnumerable<TmuxEvent> Events => _events.ReadAllAsync();

    public bool IsRunning => Volatile.Read(ref _stopRequested) == 0 && !_process.HasExited;

    [UnsupportedOSPlatform("windows")]
    internal static ControlModeSession Start(
        string tmuxBinaryPath,
        IReadOnlyList<string> prefixArguments,
        string? target,
        ServerGeneration generation,
        Action<ProcessStartInfo> configureEnvironment)
    {
        ProcessStartInfo startInfo = new(tmuxBinaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in prefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("-C");

        // Attaching is not decoration. A control client that never attaches is
        // told about the hierarchy but not about pane output, so %output never
        // arrives and the stream looks mysteriously quiet.
        startInfo.ArgumentList.Add("attach-session");
        if (target is not null)
        {
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(target);
        }

        configureEnvironment(startInfo);
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The tmux control client did not start.");
        var limits = new ControlModeLimits();
        return new ControlModeSession(
            new SystemControlModeProcess(process, limits),
            generation: generation,
            limits: limits);
    }

    /// <summary>Waits until tmux has answered its own attach.</summary>
    internal Task WaitForReadyAsync(CancellationToken cancellationToken) =>
        _ready.Task.WaitAsync(cancellationToken);

    public Task<IReadOnlyList<string>> SendAsync(
        TmuxCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateGeneration(command);
        ThrowIfStopping();
        if (_process.HasExited)
        {
            throw new InvalidOperationException("The tmux control client has exited.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_pendingSlots.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException(
                $"The control-mode session reached its {_limits.MaxPendingCommands}-command pending limit.");
        }

        return SendAdmittedAsync(command, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> SendAdmittedAsync(
        TmuxCommand command,
        CancellationToken cancellationToken)
    {
        bool transferredSlot = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sentinel = _sentinelFactory();
            if (string.IsNullOrEmpty(sentinel)
                || sentinel.Contains('\0', StringComparison.Ordinal)
                || sentinel.Contains('\r', StringComparison.Ordinal)
                || sentinel.Contains('\n', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The control-mode request fence is invalid.");
            }

            long requestBytes = ControlModeCommandRenderer.GetRenderedByteCount(command)
                + Encoding.UTF8.GetByteCount(sentinel)
                + 2L;
            if (requestBytes > _limits.MaxRequestBytes)
            {
                throw new ArgumentException(
                    $"The control-mode request exceeds its {_limits.MaxRequestBytes}-byte limit.",
                    nameof(command));
            }

            var pending = new PendingControlModeCommand(command, sentinel);
            Task<IReadOnlyList<string>> transaction = DispatchAndWaitAsync(
                command,
                pending,
                cancellationToken);
            transferredSlot = true;
            return cancellationToken.CanBeCanceled
                ? await WaitForCallerAsync(transaction, pending, cancellationToken)
                    .ConfigureAwait(false)
                : await transaction.ConfigureAwait(false);
        }
        finally
        {
            if (!transferredSlot)
            {
                _pendingSlots.Release();
            }
        }
    }

    private async Task<IReadOnlyList<string>> DispatchAndWaitAsync(
        TmuxCommand command,
        PendingControlModeCommand pending,
        CancellationToken cancellationToken)
    {
        Exception? dispatchFailure = null;
        bool ownsPendingSlot = true;

        try
        {
            // Queueing and writing happen together under one lock. tmux answers in
            // the order it was asked, so a caller that queued second and wrote
            // first would be handed the other caller's answer.
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_pending)
                {
                    ThrowIfStopping();
                    if (_process.HasExited)
                    {
                        throw new InvalidOperationException(
                            "The tmux control client has exited.");
                    }

                    // Queued before the write: a command such as kill-server can end
                    // the client as its own answer, and the pump's exit sweep must
                    // find this waiter already queued to fail it.
                    _pending.Enqueue(pending);
                    ownsPendingSlot = false;
                    pending.MarkEnqueued();
                }

                try
                {
                    await WriteRequestAsync(command, pending.Sentinel).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    // A failed pipe write may have dispatched any prefix, including
                    // the whole command. No later reply can be correlated safely.
                    Volatile.Write(ref _stopRequested, 1);
                    dispatchFailure = error;
                    FailPending(new InvalidOperationException(
                        "The control client lost command alignment after an ambiguous write failure.",
                        error));
                    _ = pending.Completion.Task.Exception;
                }
            }
            finally
            {
                _writeLock.Release();
            }

            if (dispatchFailure is not null)
            {
                try
                {
                    await DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    dispatchFailure.Data["LibTmux.ControlModeCleanupFailure"] = cleanupFailure;
                }

                ExceptionDispatchInfo.Capture(dispatchFailure).Throw();
            }

            return await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            if (ownsPendingSlot)
            {
                _pendingSlots.Release();
            }
        }
    }

    private async Task WriteRequestAsync(TmuxCommand command, string sentinel)
    {
        string framedCommand = $"{ControlModeCommandRenderer.Render(command)}\n{sentinel}";
        await _process.WriteLineAsync(framedCommand.AsMemory(), CancellationToken.None)
            .ConfigureAwait(false);
        await _process.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> WaitForCallerAsync(
        Task<IReadOnlyList<string>> transaction,
        PendingControlModeCommand pending,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transaction.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            pending.Abandon();
            await Task.WhenAny(transaction, pending.Enqueued).ConfigureAwait(false);
            _ = transaction.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                    | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private void ValidateGeneration(TmuxCommand command)
    {
        if (command.RequiredGeneration is not ServerGeneration expected)
        {
            return;
        }

        if (_generation is not ServerGeneration actual)
        {
            throw new InvalidOperationException(
                "The control client has no server generation to validate the command against.");
        }

        if (expected != actual)
        {
            throw new StaleServerGenerationException(
                "The command targets a different tmux server generation.",
                expected,
                actual);
        }
    }

    private static string CreateSentinel() =>
        $"libtmux-control-{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";

    public async ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_disposeGate)
        {
            Volatile.Write(ref _stopRequested, 1);
            disposal = _disposeTask ??= DisposeCoreAsync();
        }

        await disposal.ConfigureAwait(false);
    }

    private Task DisposeCoreAsync() => new ControlModeDisposer(
        _process,
        _writeLock,
        _pump,
        _disposalBudget,
        _timeProvider).DisposeAsync();

    private void ThrowIfStopping() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopRequested) != 0, this);

    private async Task PumpAsync()
    {
        string? exitReason = null;
        Exception? pumpFailure = null;
        try
        {
            while (await _process.ReadLineAsync().ConfigureAwait(false) is string line)
            {
                if (line.StartsWith("%begin ", StringComparison.Ordinal))
                {
                    if (!ControlModeGuard.TryParse(line, out ControlModeGuard begin)
                        || begin.Kind != ControlModeGuardKind.Begin)
                    {
                        throw new InvalidDataException(
                            "The tmux control client sent a malformed block guard.");
                    }

                    await ReadBlockAsync(begin).ConfigureAwait(false);
                    continue;
                }

                if (!line.StartsWith('%'))
                {
                    // tmux prints nothing outside a block that is not a
                    // notification, so anything here is a protocol the reader
                    // does not know rather than data to guess at.
                    continue;
                }

                (string name, IReadOnlyList<string> arguments) = SplitNotification(line);
                if (string.Equals(name, "exit", StringComparison.Ordinal))
                {
                    exitReason = arguments.Count == 0 ? null : string.Join(' ', arguments);
                    break;
                }

                _events.TryWrite(ToEvent(name, arguments));
            }
        }
        catch (Exception error)
        {
            pumpFailure = error;
            throw;
        }
        finally
        {
            _events.TryWrite(new TmuxExitEvent(exitReason));
            _events.Complete();
            Exception terminalFailure = pumpFailure ?? new InvalidOperationException(
                WithStandardError(
                    "The tmux control client exited before it finished attaching."));
            _ready.TrySetException(terminalFailure);
            StopAndFailPending(terminalFailure);
        }
    }

    private async Task ReadBlockAsync(ControlModeGuard begin)
    {
        // Only a matching %end or %error terminates a block; output may start with %.
        List<string> lines = [];
        int blockBytes = 0;
        bool failed = false;
        bool terminated = false;

        while (await _process.ReadLineAsync().ConfigureAwait(false) is string line)
        {
            if (ControlModeGuard.TryParse(line, out ControlModeGuard guard)
                && guard.Matches(begin))
            {
                if (guard.Kind == ControlModeGuardKind.End)
                {
                    terminated = true;
                    break;
                }

                if (guard.Kind == ControlModeGuardKind.Error)
                {
                    failed = true;
                    terminated = true;
                    break;
                }
            }

            if (lines.Count >= _limits.MaxBlockLines)
            {
                throw new InvalidDataException(
                    $"A control-mode block exceeded its {_limits.MaxBlockLines}-line limit.");
            }

            int lineBytes = Encoding.UTF8.GetByteCount(line);
            if (lineBytes > _limits.MaxBlockBytes - blockBytes)
            {
                throw new InvalidDataException(
                    $"A control-mode block exceeded its {_limits.MaxBlockBytes}-byte limit.");
            }

            lines.Add(line);
            blockBytes += lineBytes;
        }

        if (!terminated)
        {
            throw new InvalidDataException(
                "The tmux control client ended before its command block was terminated.");
        }

        // Attach's reply is the readiness block; enqueuing it shifts every later reply.
        InvalidOperationException? attachFailure = failed
            ? new InvalidOperationException(WithStandardError(
                lines.Count == 0 ? "The tmux attach failed." : string.Join('\n', lines)))
            : null;
        bool completedReadiness = attachFailure is null
            ? _ready.TrySetResult()
            : _ready.TrySetException(attachFailure);
        if (completedReadiness)
        {
            return;
        }

        // Hooks use flag 0 and may interleave with the caller's work. Only
        // control-input commands use flag 1 and belong to a pending request.
        if (begin.Flags != 1)
        {
            return;
        }

        PendingControlModeCommand? pending;
        lock (_pending)
        {
            pending = _pending.Count == 0 ? null : _pending.Peek();
        }

        if (pending is null)
        {
            throw new InvalidDataException(
                "The tmux control client sent a command block with no pending request.");
        }

        string sentinelError = $"parse error: unknown command: {pending.Sentinel}";
        if (failed && lines.Count == 1 && string.Equals(
            lines[0],
            sentinelError,
            StringComparison.Ordinal))
        {
            CompletePending(pending);
            return;
        }

        if (failed && lines.Any(line => line.Contains(
            pending.Sentinel,
            StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The tmux control client returned an unrecognized request fence.");
        }

        pending.AddBlock(lines, blockBytes, failed, _limits);
    }

    private void CompletePending(PendingControlModeCommand pending)
    {
        lock (_pending)
        {
            if (_pending.Count == 0 || !ReferenceEquals(_pending.Peek(), pending))
            {
                throw new InvalidDataException(
                    "The tmux control client lost its request boundary.");
            }

            _pending.Dequeue();
        }

        _pendingSlots.Release();
        pending.Complete();
    }

    private static (string Name, IReadOnlyList<string> Arguments) SplitNotification(string line)
    {
        string body = line[1..];
        int separator = body.IndexOf(' ', StringComparison.Ordinal);
        return separator < 0
            ? (body, [])
            : (body[..separator], body[(separator + 1)..].Split(' '));
    }

    private static TmuxEvent ToEvent(string name, IReadOnlyList<string> arguments)
    {
        if (!string.Equals(name, "output", StringComparison.Ordinal) || arguments.Count == 0)
        {
            return new TmuxNotificationEvent(name, arguments);
        }

        // Only the pane id is a word. Everything after the first space is the
        // payload, which may hold spaces of its own and is escaped the way tmux
        // escapes an option value.
        string payload = arguments.Count == 1
            ? string.Empty
            : string.Join(' ', arguments.Skip(1));
        return new TmuxOutputEvent(arguments[0], OptionParser.DecodeEscapes(payload));
    }

    private string WithStandardError(string message)
    {
        string standardError = _process.StandardErrorTail.Trim();
        return standardError.Length == 0
            ? message
            : $"{message}\nStandard error:\n{standardError}";
    }

    private void FailPending(Exception? failure = null)
    {
        failure ??= new InvalidOperationException(
            "The tmux control client exited before answering.");
        int released = 0;
        lock (_pending)
        {
            while (_pending.Count > 0)
            {
                _pending.Dequeue().Completion.TrySetException(failure);
                released++;
            }
        }

        if (released > 0)
        {
            _pendingSlots.Release(released);
        }
    }

    private void StopAndFailPending(Exception failure)
    {
        int released = 0;
        lock (_pending)
        {
            Volatile.Write(ref _stopRequested, 1);
            while (_pending.Count > 0)
            {
                _pending.Dequeue().Completion.TrySetException(failure);
                released++;
            }
        }

        if (released > 0)
        {
            _pendingSlots.Release(released);
        }
    }

}
