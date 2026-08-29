using System.Runtime.Versioning;

namespace LibTmux.Workspace;

/// <summary>Builds a tmux session from a tmuxp workspace file.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class WorkspaceBuilder
{
    private static readonly TimeSpan DefaultShellReadyTimeout = TimeSpan.FromSeconds(10);
    private readonly Server _server;
    private readonly TimeSpan _shellReadyTimeout;

    /// <summary>Initializes a builder against one server.</summary>
    /// <param name="server">The server the session is built on.</param>
    /// <param name="shellReadyTimeout">
    /// How long a pane may take to acknowledge shell input, or null for ten seconds.
    /// </param>
    public WorkspaceBuilder(Server server, TimeSpan? shellReadyTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (shellReadyTimeout is TimeSpan timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shellReadyTimeout),
                shellReadyTimeout,
                "A shell needs time to become ready.");
        }

        _server = server;
        _shellReadyTimeout = shellReadyTimeout ?? DefaultShellReadyTimeout;
    }

    /// <summary>Builds a session from a workspace.</summary>
    /// <param name="workspace">The workspace to build.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>What was built, and what could not be.</returns>
    /// <exception cref="WorkspaceFormatException">The workspace describes no session.</exception>
    /// <exception cref="TmuxWaitTimeoutException">
    /// A pane did not acknowledge shell input before its readiness timeout.
    /// </exception>
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

    private async Task<Window> FillAsync(
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

            if (pane.ShellCommands.Count > 0)
            {
                await WaitForShellAsync(target, cancellationToken).ConfigureAwait(false);
            }

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

    private async Task WaitForShellAsync(
        Pane pane,
        CancellationToken cancellationToken)
    {
        string channel = $"libtmux-workspace-ready-{Guid.NewGuid():N}";
        string binary = ShellQuote(pane.Server.ConnectionOptions.TmuxBinaryPath);
        string signal = $"{binary} wait-for -S {channel}";
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_shellReadyTimeout);

        try
        {
            await pane.SendKeysAsync(
                    new SendKeysRequest(
                        text: signal,
                        suppressHistory: true,
                        literal: true),
                    timeout.Token)
                .ConfigureAwait(false);
            await pane.Server.WaitForAsync(
                    new WaitForRequest(channel, TmuxWaitMode.Wait),
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException failure) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TmuxWaitTimeoutException(
                $"Pane {pane.Id} did not accept shell input within "
                + $"{_shellReadyTimeout.TotalSeconds:0.###} seconds.",
                _shellReadyTimeout,
                failure);
        }
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
