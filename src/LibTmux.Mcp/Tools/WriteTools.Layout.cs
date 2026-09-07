using System.ComponentModel;
using System.Runtime.Versioning;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <content>Building and arranging sessions, windows and panes.</content>
[UnsupportedOSPlatform("windows")]
internal sealed partial class WriteTools
{
    /// <summary>Creates a session.</summary>
    /// <param name="name">What to call it, or null to let tmux number it.</param>
    /// <param name="startDirectory">Where its first window starts.</param>
    /// <param name="width">Columns, when no client will attach to set them.</param>
    /// <param name="height">Rows, when no client will attach to set them.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What was created.</returns>
    [Description(
        "Create a detached tmux session and return its ids. Give a width and height "
        + "when nothing will attach to it: a session with no client keeps tmux's "
        + "default 80x24, which truncates wide output.")]
    public async Task<ActionResult> CreateSessionAsync(
        [Description("The session name. It cannot contain a colon or a full stop.")]
        string? name = null,
        [Description("The directory its first window starts in.")]
        string? startDirectory = null,
        [Description("Columns. Omit to accept tmux's default of 80.")] int? width = null,
        [Description("Rows. Omit to accept tmux's default of 24.")] int? height = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(null, cancellationToken).ConfigureAwait(false);
        try
        {
            Session session = await server.CreateSessionAsync(
                    new NewSessionRequest(
                        name: name,
                        startDirectory: startDirectory,
                        width: width?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        height: height?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    cancellationToken)
                .ConfigureAwait(false);

            Pane? active = session.ActivePane;
            string landed = await TmuxTargets
                .StartDirectoryNoteAsync(active, startDirectory, cancellationToken)
                .ConfigureAwait(false);
            return new ActionResult(
                $"Created session {session.Id}.{landed}",
                PaneId: active?.Id.ToString(),
                WindowId: session.ActiveWindow?.Id.ToString(),
                SessionId: session.Id.ToString());
        }
        catch (TmuxSessionExistsException)
        {
            throw new McpException(
                $"A session named '{name}' already exists. Pick another name, or use it "
                + "as it is — list_sessions shows what is there.");
        }
    }

    /// <summary>Creates a window in a session.</summary>
    /// <param name="session">The session, or null for the first one.</param>
    /// <param name="name">What to call the window.</param>
    /// <param name="startDirectory">Where it starts.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What was created.</returns>
    [Description("Create a window in a tmux session and return its ids.")]
    public async Task<ActionResult> CreateWindowAsync(
        [Description("A session id such as $0, or its name. Omit for the first session.")]
        string? session = null,
        [Description("The window name.")] string? name = null,
        [Description("The directory it starts in.")] string? startDirectory = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(null, cancellationToken).ConfigureAwait(false);
        Session owner = await TmuxTargets.SessionAsync(server, session, cancellationToken)
            .ConfigureAwait(false);
        Window window = await owner.CreateWindowAsync(
                new NewWindowRequest(
                    name: name,
                    startDirectory: startDirectory),
                cancellationToken)
            .ConfigureAwait(false);

        string landed = await TmuxTargets
            .StartDirectoryNoteAsync(window.ActivePane, startDirectory, cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult(
            $"Created window {window.Id} in {owner.Id}.{landed}",
            PaneId: window.ActivePane?.Id.ToString(),
            WindowId: window.Id.ToString(),
            SessionId: owner.Id.ToString());
    }

    /// <summary>Splits a pane in two.</summary>
    /// <param name="paneId">The pane to split, or null for the active one.</param>
    /// <param name="direction">Where the new pane goes.</param>
    /// <param name="startDirectory">Where the new pane starts.</param>
    /// <param name="percentage">How much of the space the new pane takes.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The new pane.</returns>
    [Description(
        "Split a pane and return the NEW pane's id. Use that id for what you put in "
        + "it — pane ids stay valid across layout changes, where window names and "
        + "indexes do not.")]
    public async Task<ActionResult> SplitPaneAsync(
        [Description("The pane id to split, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description("Where the new pane goes: Below, Above, Left or Right.")]
        PaneDirection direction = PaneDirection.Below,
        [Description("The directory the new pane starts in.")] string? startDirectory = null,
        [Description("Percentage of the space the new pane takes, 1 to 99.")]
        int? percentage = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(null, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);

        Pane created = await pane.SplitAsync(
                new SplitPaneRequest(
                    direction: direction,
                    startDirectory: startDirectory,
                    percentage: percentage),
                cancellationToken)
            .ConfigureAwait(false);

        string landed = await TmuxTargets
            .StartDirectoryNoteAsync(created, startDirectory, cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult(
            $"Split {pane.Id}; the new pane is {created.Id}.{landed}",
            PaneId: created.Id.ToString(),
            WindowId: created.Window.Id.ToString(),
            SessionId: created.Session.Id.ToString());
    }

    /// <summary>Makes a pane the active one.</summary>
    /// <param name="paneId">The pane.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What changed.</returns>
    [Description(
        "Make a pane the active one in its window. This changes what a watching human "
        + "sees; targeting a pane by id does not require selecting it first.")]
    public async Task<ActionResult> SelectPaneAsync(
        [Description("The pane id, such as %1.")] string paneId,
        [Description("The tmux socket to use. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        await pane.SelectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ActionResult($"{pane.Id} is now the active pane.", PaneId: pane.Id.ToString());
    }

    /// <summary>Makes a window the current one.</summary>
    /// <param name="windowId">The window.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What changed.</returns>
    [Description("Make a window the current one in its session.")]
    public async Task<ActionResult> SelectWindowAsync(
        [Description("The window id, such as @1.")] string windowId,
        [Description("The tmux socket to use. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Window window = await TmuxTargets.WindowAsync(server, windowId, cancellationToken)
            .ConfigureAwait(false);
        await window.SelectAsync(cancellationToken).ConfigureAwait(false);
        return new ActionResult(
            $"{window.Id} is now the current window.",
            WindowId: window.Id.ToString());
    }

    /// <summary>Resizes a pane.</summary>
    /// <param name="paneId">The pane, or null for the active one.</param>
    /// <param name="width">Columns to set it to.</param>
    /// <param name="height">Rows to set it to.</param>
    /// <param name="zoom">Whether to zoom it to fill the window instead.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What changed.</returns>
    [Description(
        "Resize a pane, or zoom it to fill its window. Widening a pane before reading "
        + "it is the fix for output that comes back wrapped across rows.")]
    public async Task<ActionResult> ResizePaneAsync(
        [Description("The pane id, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description("Columns to set the pane to.")] int? width = null,
        [Description("Rows to set the pane to.")] int? height = null,
        [Description("Zoom the pane to fill its window, ignoring width and height.")]
        bool zoom = false,
        [Description("The tmux socket to use. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);

        Pane resized = await pane.ResizeAsync(
                new ResizePaneRequest(
                    width: width?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    height: height?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    zoom: zoom),
                cancellationToken)
            .ConfigureAwait(false);

        return new ActionResult(
            $"{resized.Id} is now {resized.Width}x{resized.Height}.",
            PaneId: resized.Id.ToString());
    }

    /// <summary>Applies a layout to a window.</summary>
    /// <param name="windowId">The window, or null for the active one.</param>
    /// <param name="layout">The layout name or a tmux layout string.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What changed.</returns>
    [Description(
        "Arrange a window's panes with a named layout — even-horizontal, "
        + "even-vertical, main-horizontal, main-vertical, tiled — or a layout string "
        + "read from list_windows.")]
    public async Task<ActionResult> SelectLayoutAsync(
        [Description("The window id, such as @1. Omit for the active window.")]
        string? windowId = null,
        [Description("A layout name such as tiled, or a tmux layout string.")]
        string? layout = null,
        [Description("The tmux socket to use. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Window window = await TmuxTargets.WindowAsync(server, windowId, cancellationToken)
            .ConfigureAwait(false);
        Window arranged = await window.SelectLayoutAsync(
                new SelectLayoutRequest(layout: layout),
                cancellationToken)
            .ConfigureAwait(false);
        return new ActionResult(
            $"Arranged window {arranged.Id}.",
            WindowId: arranged.Id.ToString());
    }

    /// <summary>Renames a session.</summary>
    /// <param name="name">The new name.</param>
    /// <param name="session">The session, or null for the first one.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What changed.</returns>
    [Description("Rename a tmux session. Its id does not change, so anything holding one still works.")]
    public async Task<ActionResult> RenameSessionAsync(
        [Description("The new name. It cannot contain a colon or a full stop.")] string name,
        [Description("A session id such as $0, or its current name. Omit for the first session.")]
        string? session = null,
        [Description("The tmux socket to use. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Session target = await TmuxTargets.SessionAsync(server, session, cancellationToken)
            .ConfigureAwait(false);
        Session renamed = await target.RenameAsync(name, cancellationToken).ConfigureAwait(false);
        return new ActionResult(
            $"Renamed session {renamed.Id}.",
            SessionId: renamed.Id.ToString());
    }

    /// <summary>Renames a window.</summary>
    /// <param name="name">The new name.</param>
    /// <param name="windowId">The window, or null for the active one.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What changed.</returns>
    [Description("Rename a tmux window. Its id does not change.")]
    public async Task<ActionResult> RenameWindowAsync(
        [Description("The new name.")] string name,
        [Description("The window id, such as @1. Omit for the active window.")]
        string? windowId = null,
        [Description("The tmux socket to use. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Window window = await TmuxTargets.WindowAsync(server, windowId, cancellationToken)
            .ConfigureAwait(false);
        Window renamed = await window.RenameAsync(name, cancellationToken).ConfigureAwait(false);
        return new ActionResult(
            $"Renamed window {renamed.Id}.",
            WindowId: renamed.Id.ToString());
    }

    /// <summary>Sets a pane's title.</summary>
    /// <param name="title">The new title.</param>
    /// <param name="paneId">The pane, or null for the active one.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What changed.</returns>
    [Description(
        "Set a pane's title. Useful for labelling panes you created so a human "
        + "watching can tell which is which.")]
    public async Task<ActionResult> SetPaneTitleAsync(
        [Description("The new title.")] string title,
        [Description("The pane id, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description("The tmux socket to use. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        Pane titled = await pane.SetTitleAsync(title, cancellationToken).ConfigureAwait(false);
        return new ActionResult($"Set the title of {titled.Id}.", PaneId: titled.Id.ToString());
    }

}
