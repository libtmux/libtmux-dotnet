using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LibTmux;

/// <summary>An open wait on a tmux <c>wait-for</c> channel.</summary>
/// <remarks>
/// <para>
/// tmux gives a signal to whoever is registered on the channel and raises the
/// channel's pending flag only when nobody is. A waiter whose client dies stays
/// registered — tmux clears waiters when the server exits and at no other time
/// — so it goes on eating signals that can no longer reach anybody. Killing a
/// waiting client to enforce a timeout therefore destroys the next signal, and
/// each timed-out retry leaves another corpse to destroy the one after that.
/// </para>
/// <para>
/// <see cref="WaitAsync" /> returning false means that attempt expired without
/// observing completion. The open wait remains owned and may already have
/// completed from a racing signal. Closing attempts withdrawal before ending
/// the local client; failed withdrawal leaves remote registration unknown.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class TmuxWaitChannel : IAsyncDisposable
{
    private readonly object _disposeGate = new();
    private readonly Server _server;
    private readonly CancellationTokenSource _waiterLifetime = new();
    private readonly Task _waiter;
    private Task? _disposeTask;
    private CancellationTokenSource? _closeLifetime;
    private CancellationToken _closeCallerCancellation;
    private int _disposed;
    private bool _withdrew;

    internal TmuxWaitChannel(Server server, string channel)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        _server = server;
        Channel = channel;

        // Individual attempts borrow this lifetime; only close cancels it.
        _waiter = server.WaitForAsync(
            new WaitForRequest(channel, TmuxWaitMode.Wait),
            _waiterLifetime.Token);
    }

    /// <summary>Gets the channel being waited on.</summary>
    public string Channel { get; }

    /// <summary>Gets whether the wait completed before withdrawal began.</summary>
    /// <remarks>
    /// A false value does not prove that no signal arrived. Withdrawing must
    /// signal the same channel, so tmux cannot attribute a completion that
    /// races the decision to withdraw.
    /// </remarks>
    public bool Signalled => _waiter.IsCompletedSuccessfully && !_withdrew;

    /// <summary>Waits for the signal, giving this attempt a budget.</summary>
    /// <param name="budget">How long this attempt may take.</param>
    /// <param name="cancellationToken">Abandons this attempt, not the waiter.</param>
    /// <returns>True when the channel was signalled, false when the budget ran out.</returns>
    public async Task<bool> WaitAsync(
        TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget.Ticks);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_waiter.IsCompleted)
        {
            await _waiter.ConfigureAwait(false);
            return true;
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task expiry = Task.Delay(budget, attempt.Token);
        Task first = await Task.WhenAny(_waiter, expiry).ConfigureAwait(false);
        await attempt.CancelAsync().ConfigureAwait(false);

        // A cancelled caller wins over a waiter that happened to finish in the
        // same moment, so the outcome does not depend on which raced first.
        // The open wait remains owned either way, and a later attempt observes
        // any completion. Disposal alone ends its lifetime.
        cancellationToken.ThrowIfCancellationRequested();
        if (first != _waiter)
        {
            return false;
        }

        await _waiter.ConfigureAwait(false);
        return true;
    }

    internal Task WaitUntilSignalledAsync(CancellationToken cancellationToken) =>
        _waiter.WaitAsync(cancellationToken);

    /// <summary>Withdraws the waiter and ends its owned client lifetime.</summary>
    /// <param name="cancellationToken">Ends the shared withdrawal attempt early.</param>
    /// <returns>The shared close operation, including owned local cleanup.</returns>
    /// <remarks>
    /// Withdrawal has a one-second deadline. Cancellation or failed withdrawal
    /// leaves the remote registration unknown and is reported after local cleanup.
    /// Concurrent callers share this lifetime; cancelling one closes it for all.
    /// </remarks>
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                Interlocked.Exchange(ref _disposed, 1);
                _closeLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                if (cancellationToken.IsCancellationRequested)
                {
                    CancelClose(cancellationToken);
                }

                _disposeTask = CloseCoreAsync(_closeLifetime);
            }

            return cancellationToken.CanBeCanceled
                ? new ValueTask(JoinCloseAsync(_disposeTask, cancellationToken))
                : new ValueTask(_disposeTask);
        }
    }

    /// <summary>Withdraws the waiter from tmux.</summary>
    /// <remarks>
    /// <para>
    /// Signalling the channel is how a waiter withdraws: tmux wakes the
    /// registered waiters and, because the list was not empty, leaves the
    /// pending flag down. Nothing else can deregister one.
    /// </para>
    /// <para>
    /// A signal landing between the check below and the withdrawal is woken by
    /// this waiter and then re-raised by the withdrawal itself, because by then
    /// no waiter is left to take it. That leaves the channel pending rather
    /// than empty — an extra wake for the next caller, never a lost one. tmux
    /// cannot say which signal completed this waiter in that race.
    /// </para>
    /// <para>
    /// A signal wakes every waiter on the channel and tmux offers no way to
    /// deregister one on its own, so withdrawing here also completes any other
    /// wait open on the same channel. Keep one open wait per channel.
    /// </para>
    /// <para>
    /// A wait that did not dispatch or returned a command failure never
    /// registered, so disposal does not signal it. Close uses a one-second
    /// withdrawal deadline, then ends the owned local client. Failed withdrawal
    /// reports unknown remote registration; unrelated waiter failures remain observable.
    /// </para>
    /// </remarks>
    public ValueTask DisposeAsync() => CloseAsync();

    private async Task JoinCloseAsync(Task closing, CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => CancelClose(cancellationToken));
        await closing.ConfigureAwait(false);
    }

    private void CancelClose(CancellationToken cancellationToken)
    {
        lock (_disposeGate)
        {
            if (_closeLifetime is not null)
            {
                if (!_closeCallerCancellation.CanBeCanceled)
                {
                    _closeCallerCancellation = cancellationToken;
                }

                _closeLifetime.Cancel();
            }
        }
    }

    private async Task CloseCoreAsync(CancellationTokenSource lifetime)
    {
        Exception? withdrawalFailure = null;
        Exception? waiterFailure = null;
        CancellationToken callerCancellation;
        bool deadlineExpired;
        try
        {
            if (WaitMayRemainRegistered())
            {
                _withdrew = true;
                try
                {
                    await _server.WaitForAsync(
                            new WaitForRequest(Channel, TmuxWaitMode.Signal),
                            lifetime.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    withdrawalFailure = failure;
                }
            }
        }
        finally
        {
            waiterFailure = await EndOwnedWaiterAsync().ConfigureAwait(false);
            lock (_disposeGate)
            {
                callerCancellation = _closeCallerCancellation;
                deadlineExpired = lifetime.IsCancellationRequested;
                _closeLifetime = null;
            }

            lifetime.Dispose();
            _waiterLifetime.Dispose();
        }

        if (withdrawalFailure is not null)
        {
            const string Message = "The wait channel could not be withdrawn; the remote wait registration is unknown.";
            Exception cause = waiterFailure is null ? withdrawalFailure : new AggregateException(withdrawalFailure, waiterFailure);
            if (withdrawalFailure is OperationCanceledException)
            {
                if (callerCancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException(Message, cause, callerCancellation);
                }

                if (deadlineExpired)
                {
                    throw new TimeoutException(Message, cause);
                }
            }

            throw new TmuxTransportException(Message, ["wait-for", "-S", Channel], TmuxDispatchState.Unknown, cause);
        }

        if (waiterFailure is not null)
        {
            ExceptionDispatchInfo.Capture(waiterFailure).Throw();
        }
    }

    private async Task<Exception?> EndOwnedWaiterAsync()
    {
        bool wasPending = !_waiter.IsCompleted;
        Exception? failure = null;
        try
        {
            await _waiterLifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception cancellationFailure)
        {
            failure = cancellationFailure;
        }

        try
        {
            await _waiter.ConfigureAwait(false);
        }
        catch (TmuxCommandException)
        {
            // A command failure did not leave a registered waiter.
        }
        catch (StaleServerGenerationException)
        {
            // The generation guard rejected the command before registration.
        }
        catch (OperationCanceledException cancellation) when (wasPending
            && _waiterLifetime.IsCancellationRequested
            && (cancellation.CancellationToken == _waiterLifetime.Token || cancellation is TmuxOperationCanceledException))
        {
            // Only cancellation of the owned client is cleanup-only.
        }
        catch (Exception waiterFailure)
        {
            failure = failure is null ? waiterFailure : new AggregateException(failure, waiterFailure);
        }

        return failure;
    }

    private bool WaitMayRemainRegistered()
    {
        if (!_waiter.IsCompleted)
        {
            return true;
        }

        if (_waiter.IsCompletedSuccessfully)
        {
            return false;
        }

        if (!_waiter.IsFaulted)
        {
            return true;
        }

        Exception failure = _waiter.Exception!.GetBaseException();
        return failure is not TmuxCommandException
            && failure is not StaleServerGenerationException
            && failure is not LibTmuxException
            {
                Dispatch: TmuxDispatchState.NotDispatched,
            };
    }
}
