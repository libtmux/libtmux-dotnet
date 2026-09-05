using System.Runtime.Versioning;

namespace LibTmux.Mcp;

/// <summary>What a pane is and what is running in it.</summary>
/// <param name="PaneId">The pane's identifier, such as <c>%1</c>.</param>
/// <param name="WindowId">The window holding it, such as <c>@0</c>.</param>
/// <param name="SessionId">The session holding that window, such as <c>$0</c>.</param>
/// <param name="Index">The pane's position within its window.</param>
/// <param name="Width">Columns.</param>
/// <param name="Height">Rows.</param>
/// <param name="Title">The pane title, when it has one.</param>
/// <param name="Active">Whether it is the window's active pane.</param>
/// <param name="Dead">Whether the program in it has exited.</param>
/// <param name="Zoomed">Whether it is zoomed to fill its window.</param>
/// <param name="InMode">Whether it is in copy mode or another pane mode.</param>
/// <param name="CurrentCommand">The command tmux believes is running.</param>
/// <param name="CurrentPath">The working directory tmux reports.</param>
/// <param name="Pid">The process the pane started.</param>
/// <param name="HistorySize">Lines currently held in scrollback.</param>
/// <param name="HistoryLimit">The most lines scrollback will hold.</param>
/// <param name="IsCaller">Whether this is the pane this server runs inside.</param>
/// <remarks>
/// <paramref name="IsCaller" /> is how a model answers "which pane am I in?"
/// without a tool of its own: filter a listing for it.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed record PaneInfo(
    string PaneId,
    string WindowId,
    string SessionId,
    int Index,
    int Width,
    int Height,
    string? Title,
    bool Active,
    bool Dead,
    bool Zoomed,
    bool InMode,
    string? CurrentCommand,
    string? CurrentPath,
    int? Pid,
    int? HistorySize,
    int? HistoryLimit,
    bool IsCaller)
{
    /// <summary>Describes a pane the library has already materialized.</summary>
    /// <param name="pane">The pane to describe.</param>
    /// <param name="callerPaneId">The pane this process runs in, when it runs in one.</param>
    /// <returns>The description.</returns>
    public static PaneInfo From(Pane pane, string? callerPaneId = null)
    {
        ArgumentNullException.ThrowIfNull(pane);
        IReadOnlyDictionary<string, string?> fields = pane.RawFormatFields;
        string id = pane.Id.ToString();
        return new PaneInfo(
            PaneId: id,
            WindowId: pane.Window.Id.ToString(),
            SessionId: pane.Session.Id.ToString(),
            Index: pane.Index,
            Width: pane.Width,
            Height: pane.Height,
            Title: pane.Title,
            Active: FormatFields.Flag(fields, "pane_active"),
            Dead: FormatFields.Flag(fields, "pane_dead"),
            Zoomed: FormatFields.Flag(fields, "pane_zoomed_flag"),
            InMode: FormatFields.Flag(fields, "pane_in_mode"),
            CurrentCommand: FormatFields.Text(fields, "pane_current_command"),
            CurrentPath: FormatFields.Text(fields, "pane_current_path"),
            Pid: FormatFields.Number(fields, "pane_pid"),
            HistorySize: FormatFields.Number(fields, "history_size"),
            HistoryLimit: FormatFields.Number(fields, "history_limit"),
            IsCaller: string.Equals(id, callerPaneId, StringComparison.Ordinal));
    }
}

/// <summary>What a window is and how it is laid out.</summary>
/// <param name="WindowId">The window's identifier, such as <c>@0</c>.</param>
/// <param name="SessionId">The session holding it, such as <c>$0</c>.</param>
/// <param name="Index">The window's index within its session.</param>
/// <param name="Name">The window name.</param>
/// <param name="Width">Columns.</param>
/// <param name="Height">Rows.</param>
/// <param name="Active">Whether it is the session's current window.</param>
/// <param name="PaneCount">How many panes it holds.</param>
/// <param name="Layout">tmux's layout string for it.</param>
[UnsupportedOSPlatform("windows")]
public sealed record WindowInfo(
    string WindowId,
    string SessionId,
    int Index,
    string Name,
    int Width,
    int Height,
    bool Active,
    int? PaneCount,
    string? Layout)
{
    /// <summary>Describes a window the library has already materialized.</summary>
    /// <param name="window">The window to describe.</param>
    /// <returns>The description.</returns>
    public static WindowInfo From(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        IReadOnlyDictionary<string, string?> fields = window.RawFormatFields;
        return new WindowInfo(
            WindowId: window.Id.ToString(),
            SessionId: window.Session.Id.ToString(),
            Index: window.Index,
            Name: window.Name,
            Width: window.Width,
            Height: window.Height,
            Active: FormatFields.Flag(fields, "window_active"),
            PaneCount: FormatFields.Number(fields, "window_panes"),
            Layout: FormatFields.Text(fields, "window_layout"));
    }
}

/// <summary>What a session is.</summary>
/// <param name="SessionId">The session's identifier, such as <c>$0</c>.</param>
/// <param name="Name">The session name.</param>
/// <param name="Attached">Whether a client is attached to it.</param>
/// <param name="WindowCount">How many windows it holds.</param>
/// <param name="Width">Columns.</param>
/// <param name="Height">Rows.</param>
[UnsupportedOSPlatform("windows")]
public sealed record SessionInfo(
    string SessionId,
    string Name,
    bool Attached,
    int? WindowCount,
    int? Width,
    int? Height)
{
    /// <summary>Describes a session the library has already materialized.</summary>
    /// <param name="session">The session to describe.</param>
    /// <returns>The description.</returns>
    public static SessionInfo From(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        IReadOnlyDictionary<string, string?> fields = session.RawFormatFields;
        return new SessionInfo(
            SessionId: session.Id.ToString(),
            Name: session.Name,
            Attached: session.Attached,
            WindowCount: FormatFields.Number(fields, "session_windows"),
            Width: FormatFields.Number(fields, "session_width"),
            Height: FormatFields.Number(fields, "session_height"));
    }
}

/// <summary>What a tmux server is and how much it is holding.</summary>
/// <param name="SocketName">The socket this server answers on.</param>
/// <param name="Version">The tmux version running it.</param>
/// <param name="SessionCount">How many sessions it holds.</param>
/// <param name="WindowCount">How many windows, across every session.</param>
/// <param name="PaneCount">How many panes, across every window.</param>
/// <param name="CallerPaneId">The pane this server runs in, when it runs in one.</param>
public sealed record TmuxServerInfo(
    string? SocketName,
    string? Version,
    int SessionCount,
    int WindowCount,
    int PaneCount,
    string? CallerPaneId);
