using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using LibTmux.Internal;

namespace LibTmux;

internal interface IControlModeProcess : IDisposable
{
    public bool HasExited { get; }

    public Task WriteLineAsync(
        ReadOnlyMemory<char> command,
        CancellationToken cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken);

    public Task<string?> ReadLineAsync();

    public void CloseInput();

    public void Kill();

    public Task WaitForExitAsync(CancellationToken cancellationToken = default);
}

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
    private readonly TimeSpan _exitBudget;
    private readonly Func<string> _sentinelFactory;
    /// <summary>How many unread events are held before the oldest are dropped.</summary>
    /// <remarks>
    /// A pane can outpace any reader, and a caller may never read
    /// <see cref="Events"/> at all, so unbounded buffering has no ceiling.
    /// The buffer drops the oldest event instead of blocking, since blocking
    /// would also stall the reader that completes commands.
    /// </remarks>
    internal const int EventBufferCapacity = 4096;

    private readonly ControlModeEventBuffer _events = new(EventBufferCapacity);

    private readonly Queue<PendingCommand> _pending = new();
    private readonly SemaphoreSlim _writeLock;
    private readonly object _disposeGate = new();
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Task _pump;
    private Task? _disposeTask;
    private int _stopRequested;

    /// <summary>How long disposal waits for the client to exit before killing it.</summary>
    /// <remarks>
    /// Closing stdin asks tmux to leave. A client that does not answer -- wedged,
    /// stopped, or waiting on something -- would otherwise hang the caller's
    /// disposal forever, and disposal is the one operation that has to finish.
    /// </remarks>
    private static readonly TimeSpan DefaultExitBudget = TimeSpan.FromSeconds(5);

    internal ControlModeSession(
        IControlModeProcess process,
        SemaphoreSlim? writeLock = null,
        TimeSpan? exitBudget = null,
        ServerGeneration? generation = null,
        Func<string>? sentinelFactory = null)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _generation = generation;
        _writeLock = writeLock ?? new SemaphoreSlim(1, 1);
        _exitBudget = exitBudget ?? DefaultExitBudget;
        _sentinelFactory = sentinelFactory ?? CreateSentinel;
        if (_exitBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(exitBudget));
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
        // Draining stderr can hang when tmux hands its pipe to the longer-lived server.
        // Startup failures write too little to fill that pipe before the client exits.
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
        return new ControlModeSession(
            new SystemControlModeProcess(process),
            generation: generation);
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

        string sentinel = _sentinelFactory();
        var pending = new PendingCommand(command, sentinel);
        Task<IReadOnlyList<string>> transaction = DispatchAndWaitAsync(
            ControlModeCommandRenderer.Render(command),
            pending,
            cancellationToken);
        return cancellationToken.CanBeCanceled
            ? WaitForCallerAsync(transaction, cancellationToken)
            : transaction;
    }

    private async Task<IReadOnlyList<string>> DispatchAndWaitAsync(
        string commandLine,
        PendingCommand pending,
        CancellationToken cancellationToken)
    {
        Exception? dispatchFailure = null;

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
                    throw new InvalidOperationException("The tmux control client has exited.");
                }

                // Queued before the write: a command such as kill-server can end
                // the client as its own answer, and the pump's exit sweep must
                // find this waiter already queued to fail it.
                _pending.Enqueue(pending);
            }

            try
            {
                string framedCommand = $"{commandLine}\n{pending.Sentinel}";
                await _process.WriteLineAsync(framedCommand.AsMemory(), CancellationToken.None)
                    .ConfigureAwait(false);
                await _process.FlushAsync(CancellationToken.None).ConfigureAwait(false);
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

    private static async Task<IReadOnlyList<string>> WaitForCallerAsync(
        Task<IReadOnlyList<string>> transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transaction.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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

    private async Task DisposeCoreAsync()
    {
        var cleanupFailures = new List<Exception>();
        bool writeLockHeld = false;
        try
        {
            FailPending(new ObjectDisposedException(nameof(ControlModeSession)));
            writeLockHeld = await _writeLock.WaitAsync(_exitBudget).ConfigureAwait(false);
            if (!writeLockHeld)
            {
                await StopProcessAsync(cleanupFailures, forceStop: true).ConfigureAwait(false);
                writeLockHeld = await _writeLock.WaitAsync(_exitBudget).ConfigureAwait(false);
                if (!writeLockHeld)
                {
                    cleanupFailures.Add(new TimeoutException(
                        "The active control-mode write did not stop after its client was killed."));
                }
            }
            else
            {
                await StopProcessAsync(cleanupFailures, forceStop: false).ConfigureAwait(false);
            }

            // A sender can pass its final stopping check immediately before
            // disposal begins, then enqueue while disposal is waiting for it.
            FailPending(new ObjectDisposedException(nameof(ControlModeSession)));
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
        }
        finally
        {
            if (writeLockHeld)
            {
                try
                {
                    _writeLock.Release();
                }
                catch (Exception error)
                {
                    cleanupFailures.Add(error);
                }
            }
        }

        Exception? pumpFailure = null;
        try
        {
            await _pump.WaitAsync(_exitBudget).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            pumpFailure = error;
        }

        try
        {
            _process.Dispose();
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
        }

        try
        {
            if (writeLockHeld)
            {
                _writeLock.Dispose();
            }
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
        }

        ThrowDisposalFailures(pumpFailure, cleanupFailures);
    }

    private async Task StopProcessAsync(
        List<Exception> cleanupFailures,
        bool forceStop)
    {
        bool hasExited;
        try
        {
            hasExited = _process.HasExited;
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
            hasExited = false;
        }

        if (hasExited)
        {
            return;
        }

        if (!forceStop)
        {
            try
            {
                _process.CloseInput();
            }
            catch (InvalidOperationException) when (ProcessHasExited())
            {
                return;
            }
            catch (Exception error)
            {
                cleanupFailures.Add(error);
            }

            using var budget = new CancellationTokenSource(_exitBudget);
            try
            {
                await _process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                forceStop = true;
            }
            catch (InvalidOperationException) when (ProcessHasExited())
            {
                return;
            }
            catch (Exception error)
            {
                cleanupFailures.Add(error);
                forceStop = true;
            }
        }

        if (!forceStop)
        {
            return;
        }

        // Kills only the client, not its process tree: its server may still be
        // serving other clients.
        try
        {
            if (!ProcessHasExited())
            {
                _process.Kill();
            }
        }
        catch (InvalidOperationException) when (ProcessHasExited())
        {
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
        }

        using var forceBudget = new CancellationTokenSource(_exitBudget);
        try
        {
            await _process.WaitForExitAsync(forceBudget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (forceBudget.IsCancellationRequested)
        {
            cleanupFailures.Add(new TimeoutException(
                "The control-mode client did not exit after it was killed."));
        }
        catch (InvalidOperationException) when (ProcessHasExited())
        {
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
        }
    }

    private bool ProcessHasExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void ThrowDisposalFailures(
        Exception? pumpFailure,
        List<Exception> cleanupFailures)
    {
        if (pumpFailure is not null)
        {
            if (cleanupFailures.Count == 0)
            {
                ExceptionDispatchInfo.Capture(pumpFailure).Throw();
            }

            throw new AggregateException([pumpFailure, .. cleanupFailures]);
        }

        if (cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        }

        if (cleanupFailures.Count > 1)
        {
            throw new AggregateException(cleanupFailures);
        }
    }

    private void ThrowIfStopping() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopRequested) != 0, this);

    /// <summary>One waiting command.</summary>
    private sealed class PendingCommand(TmuxCommand command, string sentinel)
    {
        internal TmuxCommand Command { get; } = command;

        internal List<string> ErrorLines { get; } = [];

        internal List<string> OutputLines { get; } = [];

        internal string Sentinel { get; } = sentinel;

        internal TaskCompletionSource<IReadOnlyList<string>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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
                "The tmux control client exited before it finished attaching.");
            _ready.TrySetException(terminalFailure);
            StopAndFailPending(terminalFailure);
        }
    }

    private async Task ReadBlockAsync(ControlModeGuard begin)
    {
        // Only a matching %end or %error terminates a block; output may start with %.
        List<string> lines = [];
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

            lines.Add(line);
        }

        if (!terminated)
        {
            throw new InvalidDataException(
                "The tmux control client ended before its command block was terminated.");
        }

        // Attach's reply is the readiness block; enqueuing it shifts every later reply.
        InvalidOperationException? attachFailure = failed
            ? new InvalidOperationException(
                lines.Count == 0 ? "The tmux attach failed." : string.Join('\n', lines))
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

        PendingCommand? pending;
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

        if (failed)
        {
            if (lines.Count == 0)
            {
                pending.ErrorLines.Add("The tmux command failed.");
            }
            else
            {
                pending.ErrorLines.AddRange(lines);
            }

            return;
        }

        pending.OutputLines.AddRange(lines);
    }

    private void CompletePending(PendingCommand pending)
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

        if (pending.ErrorLines.Count == 0)
        {
            pending.Completion.TrySetResult([.. pending.OutputLines]);
            return;
        }

        string reported = string.Join('\n', pending.ErrorLines);
        pending.Completion.TrySetException(new ControlModeCommandException(
            reported.Length == 0 ? "The tmux command failed." : reported,
            pending.Command,
            pending.OutputLines,
            pending.ErrorLines));
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

    private void FailPending(Exception? failure = null)
    {
        failure ??= new InvalidOperationException(
            "The tmux control client exited before answering.");
        lock (_pending)
        {
            while (_pending.Count > 0)
            {
                _pending.Dequeue().Completion.TrySetException(failure);
            }
        }
    }

    private void StopAndFailPending(Exception failure)
    {
        lock (_pending)
        {
            Volatile.Write(ref _stopRequested, 1);
            while (_pending.Count > 0)
            {
                _pending.Dequeue().Completion.TrySetException(failure);
            }
        }
    }

    private sealed class SystemControlModeProcess(Process process) : IControlModeProcess
    {
        public bool HasExited => process.HasExited;

        public Task WriteLineAsync(
            ReadOnlyMemory<char> command,
            CancellationToken cancellationToken) =>
            process.StandardInput.WriteLineAsync(command, cancellationToken);

        public Task FlushAsync(CancellationToken cancellationToken) =>
            process.StandardInput.FlushAsync(cancellationToken);

        public Task<string?> ReadLineAsync() => process.StandardOutput.ReadLineAsync();

        public void CloseInput() => process.StandardInput.Close();

        public void Kill() => process.Kill(entireProcessTree: false);

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            process.WaitForExitAsync(cancellationToken);

        public void Dispose() => process.Dispose();
    }
}
