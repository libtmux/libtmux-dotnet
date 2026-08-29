using System.Runtime.Versioning;

namespace LibTmux.Workspace;

/// <summary>Builds a tmux session from a tmuxp workspace file.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class WorkspaceBuilder
{
    private readonly Server _server;

    /// <summary>Initializes a builder against one server.</summary>
    /// <param name="server">The server the session is built on.</param>
    public WorkspaceBuilder(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
    }

    /// <summary>Builds a session from a workspace.</summary>
    /// <param name="workspace">The workspace to build.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>What was built, and what could not be.</returns>
    /// <exception cref="WorkspaceFormatException">The workspace describes no session.</exception>
    public async Task<WorkspaceResult> BuildAsync(
        WorkspaceFile workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(workspace.SessionName))
        {
            throw new WorkspaceFormatException("The workspace names no session.");
        }

        if (workspace.Windows.Count == 0)
        {
            throw new WorkspaceFormatException("The workspace describes no windows.");
        }

        List<string> unsupported = [];

        // tmux makes a session with one window, so the file's first window is
        // that one rather than an extra.
        WorkspaceWindow first = workspace.Windows[0];
        Session session = await _server.CreateSessionAsync(
                new NewSessionRequest(
                    name: workspace.SessionName,
                    windowName: first.WindowName,
                    startDirectory: StartDirectoryFor(first, workspace)),
                cancellationToken)
            .ConfigureAwait(false);

        await ApplyOptionsAsync(session.Options, workspace.Options, cancellationToken)
            .ConfigureAwait(false);

        List<Window> windows = [];
        IReadOnlyList<Window> existing = await session.GetWindowsAsync(cancellationToken)
            .ConfigureAwait(false);
        windows.Add(await FillAsync(existing[0], first, workspace, unsupported, cancellationToken)
            .ConfigureAwait(false));

        foreach (WorkspaceWindow described in workspace.Windows.Skip(1))
        {
            Window window = await session.CreateWindowAsync(
                    new NewWindowRequest(
                        name: described.WindowName,
                        startDirectory: StartDirectoryFor(described, workspace)),
                    cancellationToken)
                .ConfigureAwait(false);
            windows.Add(
                await FillAsync(window, described, workspace, unsupported, cancellationToken)
                    .ConfigureAwait(false));
        }

        // Selecting last means the file's focus wins over the side effects of
        // building, which leave whatever was made most recently selected.
        await SelectFocusedAsync(workspace, windows, cancellationToken).ConfigureAwait(false);
        return new WorkspaceResult(session, windows, unsupported);
    }

    private static string? StartDirectoryFor(
        WorkspaceWindow window,
        WorkspaceFile workspace) =>
        (window.Panes.Count == 0 ? null : window.Panes[0].StartDirectory)
        ?? window.StartDirectory
        ?? workspace.StartDirectory;

    private static async Task ApplyOptionsAsync(
        TmuxOptions options,
        IReadOnlyDictionary<string, string> described,
        CancellationToken cancellationToken)
    {
        foreach ((string name, string value) in described)
        {
            await options.SetAsync(new SetOptionRequest(name, value), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task SelectFocusedAsync(
        WorkspaceFile workspace,
        List<Window> windows,
        CancellationToken cancellationToken)
    {
        for (int index = workspace.Windows.Count - 1; index >= 0; index--)
        {
            if (!workspace.Windows[index].Focus)
            {
                continue;
            }

            windows[index] = await windows[index].SelectAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }
    }

    private static async Task<Window> FillAsync(
        Window window,
        WorkspaceWindow described,
        WorkspaceFile workspace,
        List<string> unsupported,
        CancellationToken cancellationToken)
    {
        string? directory = described.StartDirectory ?? workspace.StartDirectory;
        IReadOnlyList<Pane> panes = await window.GetPanesAsync(cancellationToken)
            .ConfigureAwait(false);
        Pane current = panes[0];

        for (int index = 0; index < described.Panes.Count; index++)
        {
            WorkspacePane pane = described.Panes[index];

            // The window already has one pane, so the first described pane is
            // that one and the rest are splits of it.
            Pane target = index == 0
                ? current
                : await current.SplitAsync(
                        new SplitPaneRequest(startDirectory: pane.StartDirectory ?? directory),
                        cancellationToken)
                    .ConfigureAwait(false);

            foreach (string command in pane.ShellCommands)
            {
                await target.SendTextAsync(command, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            current = target;
        }

        // The layout is applied after the panes exist, because tmux arranges
        // what is there rather than what is coming.
        if (!string.IsNullOrWhiteSpace(described.Layout))
        {
            try
            {
                window = await window.SelectLayoutAsync(
                        new SelectLayoutRequest(layout: described.Layout),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LibTmuxException failure) when (
                failure is TmuxWindowException or TmuxCommandException)
            {
                unsupported.Add(
                    $"window '{described.WindowName}' layout '{described.Layout}' "
                    + $"was rejected: {failure.Message}");
            }
        }

        await ApplyOptionsAsync(window.Options, described.Options, cancellationToken)
            .ConfigureAwait(false);

        for (int index = described.Panes.Count - 1; index >= 0; index--)
        {
            if (!described.Panes[index].Focus)
            {
                continue;
            }

            IReadOnlyList<Pane> made = await window.GetPanesAsync(cancellationToken)
                .ConfigureAwait(false);
            if (index < made.Count)
            {
                await made[index].SelectAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            break;
        }

        return await window.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }
}
