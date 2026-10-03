namespace LibTmux.Workspace;

/// <summary>Controls whether workspace panes wait for a prompt-like state.</summary>
public enum PaneReadiness
{
    /// <summary>Waits for a prompt in each pane that runs the session default shell, whatever that shell is.</summary>
    Auto = 0,

    /// <summary>Waits before commands sent to every pane that runs the session default shell.</summary>
    Always = 1,

    /// <summary>Sends workspace commands without a readiness wait.</summary>
    Never = 2,
}
