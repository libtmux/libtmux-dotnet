using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace LibTmux.Mcp;

/// <summary>Owns subscriptions and one control run for an exact tmux generation.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class HierarchyEndpointWatch : IAsyncDisposable
{
    private static readonly TimeSpan InitialRecoveryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumRecoveryDelay = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly ILogger? _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<CancellationToken, Task>? _beforeRecoveryOutcome;
    private readonly Action<bool>? _recoveryOutcomeObserved;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _subscriptionGate = new(1, 1);
    private readonly Dictionary<object, HierarchyEndpointSubscriber> _subscribers = new(
        ReferenceEqualityComparer.Instance);
    private TaskCompletionSource _subscriberAvailable = NewSignal();
    private Func<CancellationToken, Task<IControlModeSession>>? _startSession;
    private Task? _recovery;
    private WatchRun? _run;
    private StartTransition? _transition;
    private bool _invalidationPending;
    private bool _retired;

    internal HierarchyEndpointWatch(
        HierarchyWatchKey key,
        ILogger? logger,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<CancellationToken, Task>? beforeRecoveryOutcome,
        Action<bool>? recoveryOutcomeObserved)
    {
        Key = key;
        _logger = logger;
        _delay = delay;
        _beforeRecoveryOutcome = beforeRecoveryOutcome;
        _recoveryOutcomeObserved = recoveryOutcomeObserved;
    }

    internal HierarchyWatchKey Key { get; }

    internal Task EnterSubscriptionAsync(CancellationToken cancellationToken) =>
        _subscriptionGate.WaitAsync(cancellationToken);

    internal void ExitSubscription() => _subscriptionGate.Release();

    internal bool TryAddReference(
        string uri,
        object subscriberKey,
        Func<IReadOnlyList<string>, Task> announce,
        out bool added)
    {
        lock (_gate)
        {
            if (_retired)
            {
                added = false;
                return false;
            }

            bool hadNoSubscribers = _subscribers.Count == 0;
            if (!_subscribers.TryGetValue(
                subscriberKey,
                out HierarchyEndpointSubscriber? subscriber))
            {
                subscriber = new HierarchyEndpointSubscriber(
                    announce,
                    ReportSubscriberFailure);
                _subscribers.Add(subscriberKey, subscriber);
            }

            added = subscriber.Resources.TryAdd(uri, 0);
            if (hadNoSubscribers)
            {
                _subscriberAvailable.TrySetResult();
            }

            return true;
        }
    }

    internal bool TryRemoveReference(string uri, object subscriberKey)
    {
        lock (_gate)
        {
            return RemoveReferenceLocked(uri, subscriberKey);
        }
    }

    /// <summary>Drops the resource for every subscriber holding it here.</summary>
    /// <remarks>
    /// The keyless unsubscribe has no way to name one of several holders, so
    /// leaving any behind would keep a callback and a control client alive that
    /// no caller can reach.
    /// </remarks>
    internal bool RemoveAllReferences(string uri)
    {
        lock (_gate)
        {
            object[] holders = [.. _subscribers
                .Where(pair => pair.Value.Resources.ContainsKey(uri))
                .Select(static pair => pair.Key)];
            bool removed = false;
            foreach (object subscriberKey in holders)
            {
                removed |= RemoveReferenceLocked(uri, subscriberKey);
            }

            return removed;
        }
    }

    internal async Task EnsureStartedAsync(
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<CancellationToken, Task<IControlModeSession>> retainedFactory;
        lock (_gate)
        {
            if (_retired || _subscribers.Count == 0)
            {
                return;
            }

            _startSession ??= startSession;
            retainedFactory = _startSession;
        }

        StartOutcome outcome = await EnsureStartedCoreAsync(
                retainedFactory,
                cancellationToken)
            .ConfigureAwait(false);
        ObserveStartOutcome(outcome);
        EnsureRecoveryStarted(retainedFactory, outcome);
    }

    private async Task<StartOutcome> EnsureStartedCoreAsync(
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        CancellationToken startupToken = startup.Token;
        while (true)
        {
            StartTransition transition;
            bool ownsTransition;
            lock (_gate)
            {
                if (_retired || _subscribers.Count == 0)
                {
                    return StartOutcome.Unused;
                }

                if (_run is WatchRun current
                    && Volatile.Read(ref current.Ended) == 0
                    && current.Session.IsRunning)
                {
                    return StartOutcome.Started;
                }

                if (_transition is StartTransition pending)
                {
                    transition = pending;
                    ownsTransition = false;
                }
                else
                {
                    WatchRun? staleRun = _run;
                    if (staleRun is not null && _subscribers.Count > 0)
                    {
                        _invalidationPending = true;
                    }

                    transition = new StartTransition(staleRun);
                    _run = null;
                    _transition = transition;
                    ownsTransition = true;
                }
            }

            if (!ownsTransition)
            {
                try
                {
                    StartOutcome completedOutcome = await transition.Completion.Task
                        .WaitAsync(startupToken)
                        .ConfigureAwait(false);
                    if (completedOutcome is not StartOutcome.Started)
                    {
                        return completedOutcome;
                    }
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested
                        && !_lifetime.IsCancellationRequested)
                {
                    continue;
                }

                continue;
            }

            StartOutcome outcome;
            try
            {
                outcome = await StartTransitionAsync(
                        transition,
                        startSession,
                        startupToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                ClearTransition(transition);
                transition.Completion.TrySetException(error);
                _ = transition.Completion.Task.Exception;
                throw;
            }

            ClearTransition(transition);
            transition.Completion.TrySetResult(outcome);
            return outcome;
        }
    }

    internal async Task<bool> StopIfUnusedAsync()
    {
        bool cancelLifetime = false;
        while (true)
        {
            StartTransition? transition;
            WatchRun? run = null;
            Task? recovery = null;
            lock (_gate)
            {
                if (_subscribers.Count > 0)
                {
                    return false;
                }

                if (!_retired)
                {
                    _retired = true;
                    cancelLifetime = true;
                }

                transition = _transition;
                if (transition is null)
                {
                    run = _run;
                    _run = null;
                    recovery = _recovery;
                }
            }

            if (cancelLifetime)
            {
                _lifetime.Cancel();
                cancelLifetime = false;
            }

            if (transition is not null)
            {
                try
                {
                    await transition.Completion.Task.ConfigureAwait(false);
                }
                catch
                {
                    // The subscribing caller owns the startup failure.
                }

                continue;
            }

            if (run is not null)
            {
                await DisposeRunAsync(run).ConfigureAwait(false);
                await run.Pump.ConfigureAwait(false);
            }

            if (recovery is not null)
            {
                await recovery.ConfigureAwait(false);
            }

            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _retired = true;
            foreach (HierarchyEndpointSubscriber subscriber in _subscribers.Values)
            {
                subscriber.Retire();
            }

            _subscribers.Clear();
        }

        _lifetime.Cancel();

        while (true)
        {
            StartTransition? transition;
            WatchRun? run = null;
            Task? recovery = null;
            lock (_gate)
            {
                transition = _transition;
                if (transition is null)
                {
                    run = _run;
                    _run = null;
                    recovery = _recovery;
                }
            }

            if (transition is not null)
            {
                try
                {
                    await transition.Completion.Task.ConfigureAwait(false);
                }
                catch
                {
                    // The subscribing caller owns the startup failure.
                }

                continue;
            }

            if (run is not null)
            {
                await DisposeRunAsync(run).ConfigureAwait(false);
                await run.Pump.ConfigureAwait(false);
            }

            if (recovery is not null)
            {
                await recovery.ConfigureAwait(false);
            }

            return;
        }
    }

    private async Task<StartOutcome> StartTransitionAsync(
        StartTransition transition,
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        CancellationToken cancellationToken)
    {
        if (transition.StaleRun is not null)
        {
            Volatile.Write(ref transition.StaleRun.Ended, 1);
            await ObserveCleanupAsync(transition.StaleRun).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_retired || _subscribers.Count == 0)
            {
                return StartOutcome.Unused;
            }
        }

        IControlModeSession? starting = null;
        try
        {
            IControlModeSession session = await startSession(cancellationToken)
                .ConfigureAwait(false);
            starting = session;
            await session
                .SendAsync(
                    TmuxCommand.Create("refresh-client", "-f", "ignore-size,no-output"),
                    cancellationToken)
                .ConfigureAwait(false);

            WatchRun run = new(session);
            bool keepRun;
            lock (_gate)
            {
                keepRun = !_retired && _subscribers.Count > 0;
                if (keepRun)
                {
                    _run = run;
                    run.Pump = PumpAsync(run);
                    starting = null;
                }
            }

            if (!keepRun)
            {
                starting = null;
                await session.DisposeAsync().ConfigureAwait(false);
                return StartOutcome.Unused;
            }

            return StartOutcome.Started;
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

            if (_logger is not null)
            {
                Log.ControlClientUnavailable(_logger, error, "hierarchy");
            }

            return StartOutcome.Unavailable;
        }
    }

    private void ClearTransition(StartTransition transition)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_transition, transition))
            {
                _transition = null;
            }
        }
    }

    private void EnsureRecoveryStarted(
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        StartOutcome outcome)
    {
        if (outcome is StartOutcome.Unused)
        {
            return;
        }

        lock (_gate)
        {
            if (!_retired
                && _subscribers.Count > 0
                && (_recovery is null || _recovery.IsCompleted))
            {
                int failedAttempts = outcome is StartOutcome.Unavailable ? 1 : 0;
                _recovery = RecoverAsync(startSession, failedAttempts);
            }
        }
    }

    private bool ObserveStartOutcome(StartOutcome outcome)
    {
        bool notify = false;
        bool invalidationPending;
        lock (_gate)
        {
            if (outcome is StartOutcome.Unavailable)
            {
                bool live = _run is WatchRun current
                    && Volatile.Read(ref current.Ended) == 0
                    && current.Session.IsRunning;
                if (!live && !_retired && _subscribers.Count > 0)
                {
                    _invalidationPending = true;
                }
            }
            else if (outcome is StartOutcome.Started
                && _invalidationPending
                && _run is WatchRun current
                && Volatile.Read(ref current.Ended) == 0
                && current.Session.IsRunning
                && !_retired
                && _subscribers.Count > 0)
            {
                _invalidationPending = false;
                notify = true;
            }

            invalidationPending = _invalidationPending;
        }

        if (notify)
        {
            Notify();
        }

        return invalidationPending;
    }

    private async Task RecoverAsync(
        Func<CancellationToken, Task<IControlModeSession>> startSession,
        int failedAttempts)
    {
        await Task.Yield();
        while (!_lifetime.IsCancellationRequested)
        {
            WatchRun? run;
            Task? subscriberAvailable = null;
            lock (_gate)
            {
                if (_retired)
                {
                    return;
                }

                if (_subscribers.Count == 0)
                {
                    subscriberAvailable = _subscriberAvailable.Task;
                }

                run = _run;
            }

            if (subscriberAvailable is not null)
            {
                try
                {
                    await subscriberAvailable.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            if (run is not null
                && Volatile.Read(ref run.Ended) == 0
                && run.Session.IsRunning)
            {
                try
                {
                    await run.Pump.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Recovery owns observing a failed event stream.
                }

                if (Interlocked.Exchange(ref run.RecoveryCounted, 1) == 0)
                {
                    failedAttempts = Math.Min(failedAttempts + 1, 6);
                }

                continue;
            }

            if (run is not null
                && Interlocked.Exchange(ref run.RecoveryCounted, 1) == 0)
            {
                failedAttempts = Math.Min(failedAttempts + 1, 6);
            }

            try
            {
                await _delay(RecoveryDelay(failedAttempts), _lifetime.Token)
                    .ConfigureAwait(false);
                StartOutcome outcome = await EnsureStartedCoreAsync(
                        startSession,
                        _lifetime.Token)
                    .ConfigureAwait(false);
                if (_beforeRecoveryOutcome is not null)
                {
                    await _beforeRecoveryOutcome(_lifetime.Token).ConfigureAwait(false);
                }

                bool invalidationPending = ObserveStartOutcome(outcome);
                _recoveryOutcomeObserved?.Invoke(invalidationPending);
                if (outcome is StartOutcome.Unused)
                {
                    continue;
                }

                if (outcome is StartOutcome.Unavailable)
                {
                    failedAttempts = Math.Min(failedAttempts + 1, 6);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                failedAttempts = Math.Min(failedAttempts + 1, 6);
            }
        }
    }

    private static TimeSpan RecoveryDelay(int failedAttempts)
    {
        int exponent = Math.Clamp(failedAttempts - 1, 0, 5);
        long ticks = InitialRecoveryDelay.Ticks * (1L << exponent);
        return TimeSpan.FromTicks(Math.Min(ticks, MaximumRecoveryDelay.Ticks));
    }

    private async Task PumpAsync(WatchRun run)
    {
        try
        {
            await foreach (TmuxEvent observed in run.Session.Events.ConfigureAwait(false))
            {
                if (HierarchyWatcher.InvalidatesHierarchy(observed))
                {
                    Notify();
                }
            }
        }
        catch (Exception error) when (error is LibTmuxException or OperationCanceledException)
        {
            // The client going away is how this ends.
        }
        finally
        {
            lock (_gate)
            {
                Volatile.Write(ref run.Ended, 1);
                if (ReferenceEquals(_run, run)
                    && !_retired
                    && _subscribers.Count > 0)
                {
                    _invalidationPending = true;
                }
            }

            await ObserveCleanupAsync(run).ConfigureAwait(false);
        }
    }

    private void Notify()
    {
        SubscriberNotification[] notifications;
        lock (_gate)
        {
            notifications = [.. _subscribers.Values.Select(subscriber =>
                new SubscriberNotification(
                    subscriber,
                    [.. subscriber.Resources.Keys]))];
        }

        foreach (SubscriberNotification notification in notifications)
        {
            if (notification.Resources.Count == 0)
            {
                continue;
            }

            notification.Subscriber.Enqueue(notification.Resources);
        }
    }

    private void ReportSubscriberFailure(Exception error)
    {
        if (_logger is not null)
        {
            Log.HierarchySubscriberFailed(_logger, error, Key.EndpointFingerprint);
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
            if (_logger is not null
                && Interlocked.Exchange(ref run.CleanupReported, 1) == 0)
            {
                Log.ControlClientCleanupFailed(_logger, error, "hierarchy");
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

    private bool RemoveReferenceLocked(string uri, object subscriberKey)
    {
        if (!_subscribers.TryGetValue(
                subscriberKey,
                out HierarchyEndpointSubscriber? subscriber)
            || !subscriber.Resources.Remove(uri))
        {
            return false;
        }

        subscriber.RemovePending(uri);

        if (subscriber.Resources.Count == 0)
        {
            _subscribers.Remove(subscriberKey);
            subscriber.Retire();
            if (_subscribers.Count == 0)
            {
                _subscriberAvailable = NewSignal();
            }
        }

        return true;
    }

    private static TaskCompletionSource NewSignal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record SubscriberNotification(
        HierarchyEndpointSubscriber Subscriber,
        IReadOnlyList<string> Resources);

    private sealed class StartTransition(WatchRun? staleRun)
    {
        internal WatchRun? StaleRun { get; } = staleRun;

        internal TaskCompletionSource<StartOutcome> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
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
        internal int RecoveryCounted;
    }

    private enum StartOutcome
    {
        Started,
        Unavailable,
        Unused,
    }
}

/// <summary>Identifies one exact endpoint and daemon generation.</summary>
internal readonly record struct HierarchyWatchKey(
    string EndpointFingerprint,
    ServerGeneration Generation)
{
    internal static HierarchyWatchKey From(Server server)
    {
        string endpointFingerprint = server.Connection?.GetEndpointFingerprint()
            ?? throw new InvalidOperationException("The server has no connection identity.");
        ServerGeneration generation = server.Generation
            ?? throw new InvalidOperationException("The server must be materialized.");
        return new HierarchyWatchKey(endpointFingerprint, generation);
    }

    internal static HierarchyWatchKey ForTest(
        string endpointFingerprint,
        ServerGeneration generation)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointFingerprint);
        return new HierarchyWatchKey(endpointFingerprint, generation);
    }
}
