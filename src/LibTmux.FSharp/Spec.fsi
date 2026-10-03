namespace LibTmux.FSharp

open LibTmux

/// <summary>Describes a pane split off the pane created before it.</summary>
/// <remarks>Build one from <c>SplitSpec.empty</c> with a copy-and-update expression.</remarks>
type SplitSpec =
    {
        /// <summary>Where the new pane goes, beside the pane before it; tmux puts it below when None.</summary>
        Direction: PaneDirection option
        /// <summary>The command the pane runs instead of the default shell.</summary>
        Command: string option
        /// <summary>The pane's working directory.</summary>
        Directory: string option
        /// <summary>The pane's size, in cells, or with a percent sign as a share of the space split.</summary>
        Size: string option
        /// <summary>Variables added to the pane's environment.</summary>
        Environment: Map<string, string>
    }

    /// <summary>Names the split by its command, without formatting through printf.</summary>
    override ToString: unit -> string

/// <summary>Describes a window: its first pane, then each pane split off the one before.</summary>
/// <remarks>Build one from <c>WindowSpec.named</c> or <c>WindowSpec.empty</c> with a copy-and-update expression.</remarks>
type WindowSpec =
    {
        /// <summary>The window's name; tmux names it after its command when None.</summary>
        Name: string option
        /// <summary>The command the first pane runs instead of the default shell.</summary>
        Command: string option
        /// <summary>The first pane's working directory.</summary>
        Directory: string option
        /// <summary>Variables added to the first pane's environment.</summary>
        Environment: Map<string, string>
        /// <summary>The panes split off in order, each beside the pane before it.</summary>
        Splits: SplitSpec list
    }

    /// <summary>Names the window, without formatting through printf.</summary>
    override ToString: unit -> string

/// <summary>Describes a session and its windows, for <c>Server.newSession</c>.</summary>
/// <remarks>
/// tmux gives a new session one window, which the first <c>WindowSpec</c>
/// becomes; each later one is a window of its own. A session with no windows
/// listed gets tmux's single default window.
/// </remarks>
type SessionSpec =
    {
        /// <summary>The session's name.</summary>
        Name: string
        /// <summary>The working directory of the session and its first window.</summary>
        Directory: string option
        /// <summary>Variables added to the session's environment.</summary>
        Environment: Map<string, string>
        /// <summary>The windows, in order; the first is the one tmux creates with the session.</summary>
        Windows: WindowSpec list
    }

    /// <summary>Names the session, without formatting through printf.</summary>
    override ToString: unit -> string

/// <summary>Starts split descriptions.</summary>
[<RequireQualifiedAccess>]
module SplitSpec =
    /// <summary>A split below the pane before it, running the default shell.</summary>
    val empty: SplitSpec

/// <summary>Starts window descriptions.</summary>
[<RequireQualifiedAccess>]
module WindowSpec =
    /// <summary>A window tmux names after its command, running the default shell.</summary>
    val empty: WindowSpec

    /// <summary>A named window running the default shell.</summary>
    val named: name: string -> WindowSpec

/// <summary>Starts session descriptions.</summary>
[<RequireQualifiedAccess>]
module SessionSpec =
    /// <summary>A named session with tmux's single default window.</summary>
    val named: name: string -> SessionSpec
