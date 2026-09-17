using System.Runtime.Versioning;

using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Server
{
    /// <summary>Reads every session on this server.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The sessions reported by a successful read.</returns>
    /// <exception cref="LibTmuxException">The listing failed, including an absent daemon.</exception>
    /// <remarks>A handle that has not found a live server yet discovers one first.</remarks>
    [UnsupportedOSPlatform("windows")]
    public Task<IReadOnlyList<Session>> GetSessionsAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync(
            "list-sessions",
            [],
            static (owner, row) => RelationReader.ToSession(owner, row),
            cancellationToken);

    /// <summary>Reads every session with at least one attached client.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The attached sessions reported by a successful read.</returns>
    /// <exception cref="LibTmuxException">The listing failed, including an absent daemon.</exception>
    /// <remarks>A handle that has not found a live server yet discovers one first.</remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Session>> GetAttachedSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        Server owner = await ListingOwnerAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await RelationReader.ListAsync(
                owner,
                "list-sessions",
                [],
                cancellationToken)
            .ConfigureAwait(false);
        return
        [
            .. rows
                .Where(static row => row.TryGetValue("session_attached", out string? value)
                    && value is not null
                    && value != "0")
                .Select(row => RelationReader.ToSession(owner, row)),
        ];
    }

    /// <summary>Reads every window on this server.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The windows reported by a successful read.</returns>
    /// <exception cref="LibTmuxException">The listing failed, including an absent daemon.</exception>
    /// <remarks>A handle that has not found a live server yet discovers one first.</remarks>
    [UnsupportedOSPlatform("windows")]
    public Task<IReadOnlyList<Window>> GetWindowsAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync(
            "list-windows",
            ["-a"],
            static (owner, row) => RelationReader.ToWindow(owner, row),
            cancellationToken);

    /// <summary>Reads every pane on this server.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The panes reported by a successful read.</returns>
    /// <exception cref="LibTmuxException">The listing failed, including an absent daemon.</exception>
    /// <remarks>A handle that has not found a live server yet discovers one first.</remarks>
    [UnsupportedOSPlatform("windows")]
    public Task<IReadOnlyList<Pane>> GetPanesAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync(
            "list-panes",
            ["-a"],
            static (owner, row) => RelationReader.ToPane(owner, row),
            cancellationToken);

    [UnsupportedOSPlatform("windows")]
    private async Task<IReadOnlyList<T>> ListAsync<T>(
        string listCommand,
        IReadOnlyList<string> extraArguments,
        Func<Server, IReadOnlyDictionary<string, string?>, T> project,
        CancellationToken cancellationToken)
    {
        Server owner = await ListingOwnerAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
            await RelationReader.ListAsync(owner, listCommand, extraArguments, cancellationToken)
                .ConfigureAwait(false);
        return [.. rows.Select(row => project(owner, row))];
    }

    // An endpoint that has not found a live server yet -- the one
    // CreateOwnedAsync and a testing scope hand back -- discovers it first, as
    // CaptureSnapshotAsync does. The objects it lists carry the discovered
    // handle, so their own relations read without discovering again.
    [UnsupportedOSPlatform("windows")]
    private async Task<Server> ListingOwnerAsync(CancellationToken cancellationToken) =>
        IsMaterialized ? this : await ConnectAsync(cancellationToken).ConfigureAwait(false);
}
