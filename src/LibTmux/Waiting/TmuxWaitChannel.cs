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
/// So this never abandons a live waiter. <see cref="WaitAsync" /> returning
/// false means the signal has not arrived yet, not that waiting stopped: the
/// registration still stands, and the next attempt sees a signal that landed in
/// between. Disposing withdraws the waiter deliberately, which is the only safe
/// way to stop.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class TmuxWaitChannel : IAsyncDisposable
{
    private readonly Server _server;
    private readonly Task _waiter;
    private int _disposed;
    private bool _withdrew;

    internal TmuxWaitChannel(Server server, string channel)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        _server = server;
        Channel = channel;

        // Deliberately unbound to any caller's token: the waiter outlives every
        // individual attempt, and only Dispose withdraws it.
        _waiter = server.WaitForAsync(
            new WaitForRequest(channel, TmuxWaitMode.Wait),
            CancellationToken.None);
    }

    /// <summary>Gets the channel being waited on.</summary>
    public string Channel { get; }

    /// <summary>Gets whether something really signalled the channel.</summary>
    /// <remarks>
    /// Withdrawing signals the channel too, so finishing is not the same as
    /// having been signalled: this stays false for a wait that was withdrawn.
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
        // Nothing is lost by that: the waiter is still registered, and only
        // disposal withdraws it.
        cancellationToken.ThrowIfCancellationRequested();
        if (first != _waiter)
        {
            return false;
        }

        await _waiter.ConfigureAwait(false);
        return true;
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
    /// than empty — an extra wake for the next caller, never a lost one.
    /// </para>
    /// <para>
    /// A signal wakes every waiter on the channel and tmux offers no way to
    /// deregister one on its own, so withdrawing here also completes any other
    /// wait open on the same channel. Keep one open wait per channel.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!_waiter.IsCompleted)
        {
            _withdrew = true;
            try
            {
                await _server.WaitForAsync(
                        new WaitForRequest(Channel, TmuxWaitMode.Signal),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TmuxCommandException)
            {
                // The server is gone, which withdraws every waiter it held.
            }
        }

        try
        {
            await _waiter.ConfigureAwait(false);
        }
        catch (TmuxCommandException)
        {
            // Disposal reports nothing; the waiter's outcome stopped mattering.
        }
        catch (OperationCanceledException)
        {
            // Same: the wait is being withdrawn, not observed.
        }
    }
}
