namespace LibTmux.Workspace;

/// <summary>Controls whether workspace panes wait for a prompt-like state.</summary>
public enum PaneReadiness
{
    /// <summary>Waits before workspace commands only when the session default shell is zsh.</summary>
    Auto = 0,

    /// <summary>Waits before commands sent to every pane that runs the session default shell.</summary>
    Always = 1,

    /// <summary>Sends workspace commands without a readiness wait.</summary>
    Never = 2,
}
