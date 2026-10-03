using System.Runtime.Versioning;

using LibTmux.Internal;
using LibTmux.Query;

namespace LibTmux;

// Raw tmux filters bypass the closed field catalog; malformed filters yield no
// rows, while command failures propagate.
public sealed partial class Server
{
    /// <summary>Runs a tmux-side filter over every session.</summary>
    /// <param name="filter">The raw tmux filter expression.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The sessions tmux kept.</returns>
    [UnsupportedOSPlatform("windows")]
    public Task<IReadOnlyList<Session>> SearchSessionsAsync(
        UnsafeTmuxFilter filter,
        CancellationToken cancellationToken = default) =>
        SearchAsync(
            "list-sessions",
            [],
            filter,
            static (owner, row) => RelationReader.ToSession(owner, row),
            cancellationToken);

    /// <summary>Runs a tmux-side filter over every window.</summary>
    /// <param name="filter">The raw tmux filter expression.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The windows tmux kept.</returns>
    [UnsupportedOSPlatform("windows")]
    public Task<IReadOnlyList<Window>> SearchWindowsAsync(
        UnsafeTmuxFilter filter,
        CancellationToken cancellationToken = default) =>
        SearchAsync(
            "list-windows",
            ["-a"],
            filter,
            static (owner, row) => RelationReader.ToWindow(owner, row),
            cancellationToken);

    /// <summary>Runs a tmux-side filter over every pane.</summary>
    /// <param name="filter">The raw tmux filter expression.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The panes tmux kept.</returns>
    [UnsupportedOSPlatform("windows")]
    public Task<IReadOnlyList<Pane>> SearchPanesAsync(
        UnsafeTmuxFilter filter,
        CancellationToken cancellationToken = default) =>
        SearchAsync(
            "list-panes",
            ["-a"],
            filter,
            static (owner, row) => RelationReader.ToPane(owner, row),
            cancellationToken);

    /// <summary>Runs a tmux-side filter over every attached client.</summary>
    /// <param name="filter">The raw tmux filter expression.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The clients tmux kept.</returns>
    /// <exception cref="TmuxVersionTooLowException">The server runs tmux older than 3.4, whose list-clients has no filter.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Client>> SearchClientsAsync(
        UnsafeTmuxFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return await QueryAsync<Client>(new ListingRequest(QueryTarget.Client, Unsafe: filter), cancellationToken)
            .ConfigureAwait(false);
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<IReadOnlyList<T>> SearchAsync<T>(
        string listCommand,
        IReadOnlyList<string> scope,
        UnsafeTmuxFilter filter,
        Func<Server, IReadOnlyDictionary<string, string?>, T> project,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await RelationReader
            .ListAsync(this, listCommand, [.. scope, "-f", filter.Value], cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => project(this, row))];
    }
}
