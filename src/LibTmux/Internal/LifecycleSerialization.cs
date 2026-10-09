namespace LibTmux.Internal;

internal static class LifecycleSerialization
{
    // Fixed stripes bound memory while independently opened handles share their socket's gate.
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();

    internal static async Task<T> RunAsync<T>(Server server, Func<Task<T>> body, CancellationToken cancellationToken)
    {
        string path = server.Connection?.SocketPath ?? throw new InvalidOperationException("The handle has no connection identity.");
        SemaphoreSlim gate = Gates[(int)((uint)StringComparer.Ordinal.GetHashCode(path) % (uint)Gates.Length)];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static T? Single<T>(IEnumerable<T> matches, string identity) where T : class
    {
        T[] items = [.. matches];
        return items.Length switch
        {
            0 => null,
            1 => items[0],
            _ => throw new TmuxAmbiguousMatchException(identity, items.Length),
        };
    }

    internal static async Task<FoundOrCreated<T>> PublishAsync<T>(
        IOwnedTmuxResource<T> owner, Func<T, bool> matches, string identity, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!matches(owner.Value))
            {
                throw new LibTmuxException($"The created resource did not retain identity '{identity}'.", TmuxDispatchState.Unknown);
            }
            return new FoundOrCreated<T>(owner.Value, owner);
        }
        catch (Exception failure)
        {
            await OwnedScope.PreserveCleanupAsync(failure, () => owner.DisposeAsync().AsTask()).ConfigureAwait(false);
            throw;
        }
    }
}
