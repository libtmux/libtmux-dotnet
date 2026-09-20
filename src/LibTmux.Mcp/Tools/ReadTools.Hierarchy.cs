using System.ComponentModel;
using System.Runtime.Versioning;

namespace LibTmux.Mcp;

/// <content>Reading what the server, its sessions, windows and panes are.</content>
[UnsupportedOSPlatform("windows")]
internal sealed partial class ReadTools
{
    /// <summary>Reads what the server is.</summary>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux queries.</param>
    /// <returns>The server's version and how much it holds.</returns>
    [Description(
        "Read the tmux server's version and how many sessions, windows and panes it "
        + "holds. Use to confirm a socket is alive and which tmux is running it.")]
    public async Task<TmuxServerInfo> ServerInfoAsync(
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);

        // The accessor caches a materialized handle per socket, and Version
        // is captured once, at connect - so a server that has since exited
        // would otherwise go on reporting the tmux that once answered it.
        bool alive = server.IsMaterialized
            && await server.IsAliveAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Session> sessions = alive
            ? await TmuxAvailability
                .OrEmptyAsync(server, () => server.GetSessionsAsync(cancellationToken))
                .ConfigureAwait(false)
            : [];
        IReadOnlyList<Window> windows = alive
            ? await TmuxAvailability
                .OrEmptyAsync(server, () => server.GetWindowsAsync(cancellationToken))
                .ConfigureAwait(false)
            : [];
        IReadOnlyList<Pane> panes = alive
            ? await TmuxAvailability
                .OrEmptyAsync(server, () => server.GetPanesAsync(cancellationToken))
                .ConfigureAwait(false)
            : [];

        return new TmuxServerInfo(
            SocketName: server.ConnectionOptions.SocketName ?? _connection.DefaultSocketName,
            SocketPath: server.ConnectionOptions.SocketPath,
            Version: alive ? server.Version?.ToString() : null,
            SessionCount: sessions.Count,
            WindowCount: windows.Count,
            PaneCount: panes.Count,
            CallerPaneId: await TmuxTargets
                .VerifiedCallerPaneIdAsync(server, cancellationToken)
                .ConfigureAwait(false),
            CallerPaneSocket: await TmuxTargets
                .ForeignCallerSocketAsync(server, cancellationToken)
                .ConfigureAwait(false));
    }

    /// <summary>Lists the sessions.</summary>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>Every session on the server.</returns>
    [Description(
        "List the tmux sessions. This reads names and sizes, not terminal text — to "
        + "find what a pane is showing, use search_panes.")]
    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Session> sessions = await TmuxAvailability
            .OrEmptyAsync(server, () => server.GetSessionsAsync(cancellationToken))
            .ConfigureAwait(false);
        HashSet<string> humanAttached = await HumanAttachedSessionIdsAsync(server, cancellationToken)
            .ConfigureAwait(false);
        return [.. sessions.Select(session =>
            SessionInfo.From(session, humanAttached.Contains(session.Id.ToString())))];
    }

    /// <summary>Reads one session.</summary>
    /// <param name="session">A session id or name.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>What the session is.</returns>
    internal async Task<SessionInfo> GetSessionInfoAsync(
        string session,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(null, cancellationToken).ConfigureAwait(false);
        Session found = await TmuxTargets.SessionAsync(server, session, cancellationToken)
            .ConfigureAwait(false);
        HashSet<string> humanAttached = await HumanAttachedSessionIdsAsync(server, cancellationToken)
            .ConfigureAwait(false);
        return SessionInfo.From(found, humanAttached.Contains(found.Id.ToString()));
    }

    // tmux counts a client this process opened for a wait or a capture the
    // same way it counts a human one, so a session's true attachment is read
    // from the client listing with every name PaneActivityHub owns excluded,
    // rather than from the session's own attached count.
    private async Task<HashSet<string>> HumanAttachedSessionIdsAsync(
        Server server,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Client> clients = await TmuxAvailability
            .OrEmptyAsync(server, () => server.GetClientsAsync(cancellationToken))
            .ConfigureAwait(false);
        HashSet<string> attached = new(StringComparer.Ordinal);
        foreach (Client client in clients)
        {
            if (_activity.OwnsControlClient(client.Name) || client.AttachedSessionId is not SessionId id)
            {
                continue;
            }

            attached.Add(id.ToString());
        }

        return attached;
    }

    /// <summary>Lists the windows.</summary>
    /// <param name="session">A session id or name to narrow to, or null for all of them.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The windows.</returns>
    [Description(
        "List tmux windows, optionally within one session. This reads names and "
        + "layouts, not terminal text — to find what a pane is showing, use "
        + "search_panes.")]
    public async Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(
        [Description("A session id such as $0, or its name. Omit for every session.")]
        string? session = null,
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(session))
        {
            IReadOnlyList<Window> all = await TmuxAvailability
                .OrEmptyAsync(server, () => server.GetWindowsAsync(cancellationToken))
                .ConfigureAwait(false);
            return [.. all.Select(WindowInfo.From)];
        }

        Session scoped = await TmuxTargets.SessionAsync(server, session, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<Window> windows = await scoped.GetWindowsAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. windows.Select(WindowInfo.From)];
    }

    /// <summary>Lists the panes.</summary>
    /// <param name="session">A session id or name to narrow to, or null for all of them.</param>
    /// <param name="windowId">A window id to narrow to, or null for all of them.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The panes.</returns>
    [Description(
        "List tmux panes, optionally within one session or window. Filter for "
        + "isCaller=true to answer 'which pane am I in?', which finds one only when this server drives the caller's own socket — get_server_info says whose socket that is. This reads sizes and "
        + "running commands, not terminal text — for that use search_panes.")]
    public async Task<IReadOnlyList<PaneInfo>> ListPanesAsync(
        [Description("A session id such as $0, or its name. Omit for every session.")]
        string? session = null,
        [Description("A window id such as @0. Omit for every window.")]
        string? windowId = null,
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        string? caller = await TmuxTargets
            .VerifiedCallerPaneIdAsync(server, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(windowId))
        {
            Window window = await TmuxTargets.WindowAsync(server, windowId, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<Pane> scoped = await window.GetPanesAsync(cancellationToken)
                .ConfigureAwait(false);
            return [.. scoped.Select(pane => PaneInfo.From(pane, caller))];
        }

        if (!string.IsNullOrWhiteSpace(session))
        {
            Session owner = await TmuxTargets.SessionAsync(server, session, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<Pane> scoped = await owner.GetPanesAsync(cancellationToken)
                .ConfigureAwait(false);
            return [.. scoped.Select(pane => PaneInfo.From(pane, caller))];
        }

        IReadOnlyList<Pane> panes = await TmuxAvailability
            .OrEmptyAsync(server, () => server.GetPanesAsync(cancellationToken))
            .ConfigureAwait(false);
        return [.. panes.Select(pane => PaneInfo.From(pane, caller))];
    }

}
