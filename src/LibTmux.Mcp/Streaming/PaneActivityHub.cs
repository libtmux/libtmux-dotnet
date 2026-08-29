using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace LibTmux.Mcp;

/// <summary>Tells a waiter the moment a pane prints something.</summary>
/// <remarks>
/// <para>
/// tmux will report pane output as it happens to a client in control mode, so
/// a wait can sleep until there is something to look at instead of asking
/// every few milliseconds whether anything changed. On a wait that ends up
/// timing out, that is the difference between hundreds of tmux processes and
/// none.
/// </para>
/// <para>
/// What arrives on that stream is the pane's raw terminal bytes — escape
/// sequences, redraws and all — which is why it is used as a signal and never
/// as content. The text a caller gets always comes from a capture, which is
/// what tmux has already rendered.
/// </para>
/// <para>
/// A control client sees only the session it attached to, so watches are per
/// session and reference counted. Control mode is an optimisation, not a
/// requirement: when a client cannot start, waiting falls back to polling and
/// the caller cannot tell the difference except in cost.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class PaneActivityHub : IAsyncDisposable
{
    /// <summary>How long a poll-based wait sleeps between reads.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(60);

    private readonly ConcurrentDictionary<SessionWatchKey, SessionWatch> _watches = [];
    private readonly ILogger? _logger;
    private readonly Func<Pane, CancellationToken, Task<IControlModeSession>>? _startPaneSession;
    private bool _disposed;

    /// <summary>Initializes the hub.</summary>
    /// <param name="logger">Records why a control client could not start.</param>
    public PaneActivityHub(ILogger? logger = null) => _logger = logger;

    internal PaneActivityHub(
        Func<Pane, CancellationToken, Task<IControlModeSession>> startPaneSession,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(startPaneSession);
        _startPaneSession = startPaneSession;
        _logger = logger;
    }

    /// <summary>Gets whether any session is currently watched through control mode.</summary>
    public bool IsStreaming => _watches.Values.Any(watch => watch.IsStreaming);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _disposed, true);
        foreach (KeyValuePair<SessionWatchKey, SessionWatch> entry in _watches)
        {
            await entry.Value.DisposeAsync().ConfigureAwait(false);
        }

        _watches.Clear();
    }

    /// <summary>Watches a pane's session for as long as the result is held.</summary>
    /// <param name="pane">The pane whose session to watch.</param>
    /// <param name="cancellationToken">Cancels starting the control client.</param>
    /// <returns>A lease that stops watching when disposed.</returns>
    /// <remarks>
    /// Take one of these around a wait. Without it the wait still works, by
    /// polling; with it, tmux does the waiting.
    /// </remarks>
    public async Task<IAsyncDisposable> WatchAsync(Pane pane, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pane);
        SessionWatchKey key = SessionWatchKey.From(pane);
        return await WatchAsync(
                key,
                token => StartPaneSessionAsync(pane, key.SessionId, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IControlModeSession> StartPaneSessionAsync(
        Pane pane,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return _startPaneSession is null
                ? await pane.Server
                    .EnterControlModeAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false)
                : await _startPaneSession(pane, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is Win32Exception
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            throw new TmuxTransportException(
                "The tmux control client could not attach; polling will be used instead.",
                [],
                TmuxDispatchState.NotDispatched,
                error);
        }
    }

    /// <summary>Watches a session with a supplied control-client factory.</summary>
    internal async Task<IAsyncDisposable> WatchAsync(
        string sessionId,
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        CancellationToken cancellationToken) =>
        await WatchAsync(
                SessionWatchKey.ForTest("default", sessionId),
                startSession,
                cancellationToken)
            .ConfigureAwait(false);

    internal async Task<IAsyncDisposable> WatchAsync(
        string endpointId,
        string sessionId,
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        CancellationToken cancellationToken) =>
        await WatchAsync(
                SessionWatchKey.ForTest(endpointId, sessionId),
                startSession,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<IAsyncDisposable> WatchAsync(
        SessionWatchKey key,
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key.SessionId);
        ArgumentNullException.ThrowIfNull(startSession);
        while (!Volatile.Read(ref _disposed))
        {
            SessionWatch watch = _watches.GetOrAdd(
                key,
                static (created, hub) => new SessionWatch(created, hub),
                this);

            LeaseAcquisition acquired = await watch
                .AcquireAsync(startSession, cancellationToken)
                .ConfigureAwait(false);
            if (acquired.Lease is not null)
            {
                if (Volatile.Read(ref _disposed))
                {
                    await acquired.Lease.DisposeAsync().ConfigureAwait(false);
                    return NullLease.Instance;
                }

                return acquired.Lease;
            }

            RemoveWatch(key, watch);
            if (!acquired.Retry)
            {
                return NullLease.Instance;
            }
        }

        return NullLease.Instance;
    }

    private void RemoveWatch(SessionWatchKey key, SessionWatch watch) =>
        ((ICollection<KeyValuePair<SessionWatchKey, SessionWatch>>)_watches)
            .Remove(new KeyValuePair<SessionWatchKey, SessionWatch>(key, watch));

    /// <summary>Waits until a pane prints something, or the time runs out.</summary>
    /// <param name="paneId">The pane to wait on.</param>
    /// <param name="signalBefore">
    /// The signal captured before the caller last read the pane. Passing the
    /// one taken before the read is what stops output that arrived during it
    /// from being missed.
    /// </param>
    /// <param name="timeout">How long to wait at most.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns><see langword="true" /> when the pane printed something.</returns>
    public async Task<bool> WaitForActivityAsync(
        string paneId,
        object? signalBefore,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paneId);
        if (Volatile.Read(ref _disposed) || timeout <= TimeSpan.Zero)
        {
            return false;
        }

        // Without a live control client there is nothing to be woken by, so the
        // caller sleeps a short fixed step and reads again.
        if (signalBefore is not Task wake)
        {
            TimeSpan step = timeout < PollInterval ? timeout : PollInterval;
            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
            return false;
        }

        try
        {
            await wake.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Takes the token that a later wait on this pane will wake from.</summary>
    /// <param name="paneId">The pane about to be read.</param>
    /// <returns>The token, or null when nothing is streaming this pane.</returns>
    /// <remarks>
    /// Take this <em>before</em> reading the pane. Output that arrives between
    /// the read and the wait would otherwise leave the waiter asleep with the
    /// answer already on screen.
    /// </remarks>
    public object? CaptureSignal(string paneId)
    {
        ArgumentNullException.ThrowIfNull(paneId);
        SessionWatch[] streaming = [.. _watches.Values
            .Where(watch => watch.IsStreaming)
            .Take(2)];
        return streaming.Length == 1 ? streaming[0].CaptureSignal(paneId) : null;
    }

    /// <summary>Takes the exact endpoint/session token for a pane about to be read.</summary>
    /// <param name="pane">The pane about to be read.</param>
    /// <returns>The token, or null when that pane's session is not streaming.</returns>
    public object? CaptureSignal(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return CaptureSignal(SessionWatchKey.From(pane), pane.Id.ToString());
    }

    internal Task? CaptureSignal(string endpointId, string sessionId, string paneId) =>
        CaptureSignal(SessionWatchKey.ForTest(endpointId, sessionId), paneId);

    private Task? CaptureSignal(SessionWatchKey key, string paneId) =>
        _watches.TryGetValue(key, out SessionWatch? watch)
            ? watch.CaptureSignal(paneId)
            : null;

    /// <summary>One pane's "something happened" bell.</summary>
    /// <remarks>
    /// The completion source is replaced rather than reset, so a waiter that
    /// took the previous one still completes: a bell nobody was listening for
    /// yet must not be lost.
    /// </remarks>
    private sealed class PaneSignal
    {
        private TaskCompletionSource _source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Current => Volatile.Read(ref _source).Task;

        internal void Fire() =>
            Interlocked.Exchange(
                    ref _source,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
    }

    /// <summary>One session's control client, and how many waits need it.</summary>
    private sealed class SessionWatch(SessionWatchKey key, PaneActivityHub hub) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, PaneSignal> _signals = new(StringComparer.Ordinal);
        private readonly object _signalGate = new();
        private WatchRun? _run;
        private bool _retired;
        private int _leases;

        internal bool IsStreaming
        {
            get
            {
                WatchRun? run = Volatile.Read(ref _run);
                return run is not null
                    && Volatile.Read(ref run.Ended) == 0
                    && run.Session.IsRunning;
            }
        }

        internal async Task<LeaseAcquisition> AcquireAsync(
            Func<CancellationToken, Task<IControlModeSession>> startSession,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            IControlModeSession? starting = null;
            try
            {
                if (_retired)
                {
                    return LeaseAcquisition.RetryRequired;
                }

                if (_run is WatchRun current)
                {
                    if (Volatile.Read(ref current.Ended) == 0 && current.Session.IsRunning)
                    {
                        _leases = checked(_leases + 1);
                        return new LeaseAcquisition(new Release(this), Retry: false);
                    }

                    Volatile.Write(ref current.Ended, 1);
                    _run = null;
                    StopSignaling();
                    await ObserveCleanupAsync(current).ConfigureAwait(false);
                }

                starting = await startSession(cancellationToken).ConfigureAwait(false);

                // A listening client must ignore size or it can shrink the session's windows.
                // The flag is available throughout the supported tmux range.
                await starting.SendAsync(
                    TmuxCommand.Create("refresh-client", "-f", "ignore-size"),
                    cancellationToken)
                    .ConfigureAwait(false);

                WatchRun run = new(starting);
                _run = run;
                run.Pump = PumpAsync(run);
                starting = null;
                _leases = checked(_leases + 1);
                return new LeaseAcquisition(new Release(this), Retry: false);
            }
            catch (Exception startupFailure)
            {
                if (starting is not null)
                {
                    try
                    {
                        await starting.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure)
                    {
                        startupFailure.Data["LibTmux.ControlModeCleanupFailure"] = cleanupFailure;
                    }
                }

                if (startupFailure is not LibTmuxException error)
                {
                    throw;
                }

                if (hub._logger is not null)
                {
                    Log.ControlClientUnavailable(hub._logger, error, key.SessionId);
                }

                _retired = true;
                return LeaseAcquisition.Unavailable;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            WatchRun? run;
            try
            {
                _retired = true;
                run = _run;
                _run = null;
            }
            finally
            {
                _gate.Release();
            }

            if (run is not null)
            {
                StopSignaling();
                await DisposeRunAsync(run).ConfigureAwait(false);
                await run.Pump.ConfigureAwait(false);
            }

        }

        private async Task PumpAsync(WatchRun run)
        {
            try
            {
                await foreach (TmuxEvent observed in run.Session.Events.ConfigureAwait(false))
                {
                    switch (observed)
                    {
                        case TmuxOutputEvent output:
                            OnPaneOutput(output.PaneId);
                            break;
                        case TmuxExitEvent exit when hub._logger is not null:
                            Log.ControlClientEnded(hub._logger, key.SessionId, exit.Reason);
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (Exception error) when (error is LibTmuxException or OperationCanceledException)
            {
                // The client going away is how this ends. Waiters fall back to
                // their own timeout, which is why losing the stream degrades
                // cost rather than correctness.
            }
            finally
            {
                await MarkEndedAsync(run).ConfigureAwait(false);
                await ObserveCleanupAsync(run).ConfigureAwait(false);
            }
        }

        private async Task MarkEndedAsync(WatchRun run)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Volatile.Write(ref run.Ended, 1);
                StopSignaling();
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task ObserveCleanupAsync(WatchRun run)
        {
            try
            {
                await DisposeRunAsync(run).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (hub._logger is not null
                    && Interlocked.Exchange(ref run.CleanupReported, 1) == 0)
                {
                    Log.ControlClientCleanupFailed(hub._logger, error, key.SessionId);
                }
            }
        }

        private static async Task DisposeRunAsync(WatchRun run)
        {
            if (Interlocked.CompareExchange(ref run.DisposalStarted, 1, 0) != 0)
            {
                await run.Disposal.Task.ConfigureAwait(false);
                return;
            }

            try
            {
                await run.Session.DisposeAsync().ConfigureAwait(false);
                run.Disposal.TrySetResult();
            }
            catch (Exception error)
            {
                run.Disposal.TrySetException(error);
                _ = run.Disposal.Task.Exception;
                throw;
            }
        }

        private async ValueTask ReleaseOneAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            WatchRun? run = null;
            try
            {
                if (_retired)
                {
                    return;
                }

                _leases--;
                if (_leases > 0)
                {
                    return;
                }

                _retired = true;
                run = _run;
                _run = null;
            }
            finally
            {
                _gate.Release();
            }

            hub.RemoveWatch(key, this);
            if (run is not null)
            {
                StopSignaling();
                await DisposeRunAsync(run).ConfigureAwait(false);
                await run.Pump.ConfigureAwait(false);
            }
        }

        internal Task? CaptureSignal(string paneId)
        {
            lock (_signalGate)
            {
                if (!IsStreaming)
                {
                    return null;
                }

                if (!_signals.TryGetValue(paneId, out PaneSignal? signal))
                {
                    signal = new PaneSignal();
                    _signals.Add(paneId, signal);
                }

                return signal.Current;
            }
        }

        private void OnPaneOutput(string paneId)
        {
            lock (_signalGate)
            {
                if (_signals.TryGetValue(paneId, out PaneSignal? signal))
                {
                    signal.Fire();
                }
            }
        }

        private void StopSignaling()
        {
            lock (_signalGate)
            {
                foreach (PaneSignal signal in _signals.Values)
                {
                    signal.Fire();
                }

                _signals.Clear();
            }
        }

        private sealed class Release(SessionWatch watch) : IAsyncDisposable
        {
            private int _done;

            public ValueTask DisposeAsync() =>
                Interlocked.Exchange(ref _done, 1) == 0
                    ? watch.ReleaseOneAsync()
                    : ValueTask.CompletedTask;
        }

        private sealed class WatchRun(IControlModeSession session)
        {
            internal IControlModeSession Session { get; } = session;

            internal Task Pump { get; set; } = Task.CompletedTask;

            internal TaskCompletionSource Disposal { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            internal int Ended;
            internal int DisposalStarted;
            internal int CleanupReported;
        }
    }

    private sealed class NullLease : IAsyncDisposable
    {
        internal static NullLease Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly record struct LeaseAcquisition(IAsyncDisposable? Lease, bool Retry)
    {
        internal static LeaseAcquisition RetryRequired { get; } = new(null, Retry: true);

        internal static LeaseAcquisition Unavailable { get; } = new(null, Retry: false);
    }

    private readonly record struct SessionWatchKey(
        Server? Server,
        ServerGeneration? Generation,
        string? TestEndpoint,
        string SessionId)
    {
        internal static SessionWatchKey From(Pane pane) =>
            new(pane.Server, pane.Generation, TestEndpoint: null, pane.Session.Id.ToString());

        internal static SessionWatchKey ForTest(string endpointId, string sessionId) =>
            new(Server: null, Generation: null, endpointId, sessionId);
    }
}
