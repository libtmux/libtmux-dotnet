using System.Runtime.Versioning;

namespace LibTmux.Workspace;

/// <summary>Builds a tmux session from a tmuxp workspace file.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class WorkspaceBuilder
{
    private const string BootstrapWindowName = "libtmux-bootstrap";
    private static readonly TimeSpan DefaultReadinessTimeout = TimeSpan.FromSeconds(10);
    private readonly Server _server;
    private readonly PaneReadiness _paneReadiness;
    private readonly TimeSpan _readinessTimeout;

    /// <summary>Initializes a builder against one server.</summary>
    /// <param name="server">The server the session is built on.</param>
    /// <param name="readinessTimeout">
    /// How long a pane may take to reach a prompt-like state, or null for ten seconds.
    /// </param>
    /// <param name="paneReadiness">Which default-shell panes wait for readiness.</param>
    public WorkspaceBuilder(
        Server server,
        TimeSpan? readinessTimeout = null,
        PaneReadiness paneReadiness = PaneReadiness.Auto)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (readinessTimeout is TimeSpan timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readinessTimeout),
                readinessTimeout,
                "A readiness timeout must be positive.");
        }

        if (!Enum.IsDefined(paneReadiness))
        {
            throw new ArgumentOutOfRangeException(
                nameof(paneReadiness),
                paneReadiness,
                "The pane-readiness policy is not defined.");
        }

        _server = server;
        _readinessTimeout = readinessTimeout ?? DefaultReadinessTimeout;
        _paneReadiness = paneReadiness;
    }

    /// <summary>Builds a session from a workspace.</summary>
    /// <param name="workspace">The workspace to build.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>What was built, and what could not be.</returns>
    /// <exception cref="WorkspaceFormatException">The workspace describes no session.</exception>
    /// <exception cref="WorkspaceBuildException">
    /// tmux failed after application began. The exception reports any materialized state.
    /// </exception>
    /// <remarks>
    /// Readiness is inferred from the pane's current command and cursor position.
    /// Startup output can resemble a prompt, and a prompt left at the origin can
    /// time out; the builder never writes a readiness probe to the pane.
    /// </remarks>
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

        Session? session = null;
        List<Window> windows = [];
        List<string> unsupported = [];
        try
        {
            WorkspaceWindow first = workspace.Windows[0];
            // tmux starts the first pane before session options exist. A
            // bootstrap keeps the session alive until the real window exists.
            session = await _server.CreateSessionAsync(
                    new NewSessionRequest(
                        name: workspace.SessionName,
                        windowName: BootstrapWindowName,
                        startDirectory: StartDirectoryFor(first, workspace),
                        command: "/bin/sh"),
                    cancellationToken)
                .ConfigureAwait(false);
            return await CompleteAsync(
                    session,
                    workspace,
                    windows,
                    unsupported,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            WorkspaceResult? partial = session is null
                ? null
                : new WorkspaceResult(session, windows, unsupported);
            throw new WorkspaceBuildException(partial, failure);
        }
    }

    private async Task<WorkspaceResult> CompleteAsync(
        Session session,
        WorkspaceFile workspace,
        List<Window> windows,
        List<string> unsupported,
        CancellationToken cancellationToken)
    {
        WorkspaceWindow first = workspace.Windows[0];

        await ApplyOptionsAsync(session.Options, workspace.Options, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<Window> bootstrapWindows = await session.GetWindowsAsync(cancellationToken)
            .ConfigureAwait(false);
        Window bootstrap = bootstrapWindows.Count == 1
            ? bootstrapWindows[0]
            : throw new InvalidDataException(
                "tmux did not report exactly one bootstrap window.");
        string firstIndex = await ReadOptionAsync(
                session.Options,
                "base-index",
                false,
                cancellationToken)
            .ConfigureAwait(false);
        string? expectedShellCommand = await ResolveReadinessShellAsync(
                session,
                cancellationToken)
            .ConfigureAwait(false);
        Window firstWindow = await session.CreateWindowAsync(
                new NewWindowRequest(
                    name: first.WindowName,
                    startDirectory: StartDirectoryFor(first, workspace),
                    index: firstIndex,
                    killExisting: true),
                cancellationToken)
            .ConfigureAwait(false);
        if (firstWindow.Index != bootstrap.Index)
        {
            await bootstrap.KillAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        windows.Add(firstWindow);
        windows[0] = await FillAsync(
                firstWindow,
                first,
                workspace,
                unsupported,
                expectedShellCommand,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (WorkspaceWindow described in workspace.Windows.Skip(1))
        {
            Window window = await session.CreateWindowAsync(
                    new NewWindowRequest(
                        name: described.WindowName,
                        startDirectory: StartDirectoryFor(described, workspace)),
                    cancellationToken)
                .ConfigureAwait(false);
            windows.Add(window);
            windows[^1] = await FillAsync(
                    window,
                    described,
                    workspace,
                    unsupported,
                    expectedShellCommand,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Selecting last means the file's focus wins over the side effects of
        // building, which leave whatever was made most recently selected.
        await SelectFocusedAsync(workspace, windows, cancellationToken).ConfigureAwait(false);
        return new WorkspaceResult(
            await session.RefreshAsync(cancellationToken).ConfigureAwait(false),
            windows,
            unsupported);
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

    private async Task<string?> ResolveReadinessShellAsync(
        Session session,
        CancellationToken cancellationToken)
    {
        if (_paneReadiness == PaneReadiness.Never)
        {
            return null;
        }

        string defaultCommand = await ReadOptionAsync(
                session.Options,
                "default-command",
                true,
                cancellationToken)
            .ConfigureAwait(false);
        if (defaultCommand.Length > 0)
        {
            return null;
        }

        string defaultShell = await ReadOptionAsync(
                session.Options,
                "default-shell",
                false,
                cancellationToken)
            .ConfigureAwait(false);
        return PaneReadinessWaiter.SelectShell(_paneReadiness, defaultCommand, defaultShell);
    }

    private static async Task<string> ReadOptionAsync(
        TmuxOptions options,
        string name,
        bool allowEmpty,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TmuxOption> reported = await options.GetAsync(
                new GetOptionRequest(name, includeInherited: true),
                cancellationToken)
            .ConfigureAwait(false);
        if (reported.Count != 1)
        {
            throw new InvalidDataException(
                $"tmux did not report exactly one value for '{name}'.");
        }

        return reported[0].Value.Raw
            ?? (allowEmpty
                ? ""
                : throw new InvalidDataException(
                    $"tmux reported no value for '{name}'."));
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
        string? expectedShellCommand,
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

            if (expectedShellCommand is not null && pane.ShellCommands.Count > 0)
            {
                await PaneReadinessWaiter.WaitAsync(
                        target,
                        expectedShellCommand,
                        _readinessTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
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
}
