using System.Runtime.Versioning;

using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Server
{
    /// <summary>Reads every session on this server.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The sessions reported by a successful read.</returns>
    /// <exception cref="LibTmuxException">The listing failed, including an absent daemon.</exception>
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
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Session>> GetAttachedSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await RelationReader.ListAsync(
                this,
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
                .Select(row => RelationReader.ToSession(this, row)),
        ];
    }

    /// <summary>Reads every window on this server.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The windows reported by a successful read.</returns>
    /// <exception cref="LibTmuxException">The listing failed, including an absent daemon.</exception>
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
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
            await RelationReader.ListAsync(this, listCommand, extraArguments, cancellationToken)
                .ConfigureAwait(false);
        return [.. rows.Select(row => project(this, row))];
    }
}
