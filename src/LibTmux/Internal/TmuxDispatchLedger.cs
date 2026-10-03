namespace LibTmux.Internal;

/// <summary>Counts the commands that reached tmux, or may have, within one asynchronous flow.</summary>
/// <remarks>
/// A failure says whether its own command reached tmux, not whether anything
/// before it in the same operation did. An operation that ran one command and
/// then failed to send a second is not safe to repeat, though its failure is
/// <see cref="TmuxDispatchState.NotDispatched" />. A retry therefore runs each
/// attempt in a ledger and repeats it only when the ledger counted nothing.
/// Ledgers nest: a command counts in every ledger it runs inside.
/// </remarks>
internal sealed class TmuxDispatchLedger
{
    private static readonly AsyncLocal<TmuxDispatchLedger?> Current = new();

    private readonly TmuxDispatchLedger? _parent;
    private int _reached;

    private TmuxDispatchLedger(TmuxDispatchLedger? parent) => _parent = parent;

    /// <summary>Gets whether any command counted here reached tmux, or may have.</summary>
    internal bool AnyReached => Volatile.Read(ref _reached) > 0;

    /// <summary>Creates a ledger inside the one the calling flow is counting in, if any.</summary>
    /// <returns>A ledger that counts nothing until work runs in it.</returns>
    internal static TmuxDispatchLedger Create() => new(Current.Value);

    /// <summary>Runs work counting every command it sends, however deeply, in a ledger.</summary>
    /// <param name="ledger">The ledger to count in.</param>
    /// <param name="work">The work to run.</param>
    /// <returns>The work's result.</returns>
    /// <remarks>
    /// The ledger is set inside this method, so it reaches everything the work
    /// awaits and nothing the caller runs afterwards.
    /// </remarks>
    internal static async Task<T> CountAsync<T>(TmuxDispatchLedger ledger, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(work);
        Current.Value = ledger;
        return await work().ConfigureAwait(false);
    }

    /// <summary>Counts a command once its sending has settled, unless it surely never reached tmux.</summary>
    /// <param name="sending">The command in flight.</param>
    /// <returns>The command's result.</returns>
    internal static async Task<T> TrackAsync<T>(Task<T> sending)
    {
        bool reached = true;
        try
        {
            return await sending.ConfigureAwait(false);
        }
        catch (LibTmuxException error) when (error.Dispatch == TmuxDispatchState.NotDispatched)
        {
            reached = false;
            throw;
        }
        finally
        {
            if (reached)
            {
                for (TmuxDispatchLedger? ledger = Current.Value; ledger is not null; ledger = ledger._parent)
                {
                    _ = Interlocked.Increment(ref ledger._reached);
                }
            }
        }
    }
}
