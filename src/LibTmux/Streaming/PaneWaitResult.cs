namespace LibTmux;

/// <summary>Names how a wait on a pane's output ended.</summary>
public enum PaneWaitOutcome
{
    /// <summary>New output matched a pattern.</summary>
    Matched = 0,

    /// <summary>A pattern already matched the screen when the wait began.</summary>
    PresentAtEntry = 1,

    /// <summary>The pane printed something, and no pattern was given.</summary>
    AnyOutput = 2,

    /// <summary>New output matched a stop pattern.</summary>
    Stopped = 3,

    /// <summary>The time allowed ran out.</summary>
    TimedOut = 4,

    /// <summary>The pane's program exited.</summary>
    PaneExited = 5,

    /// <summary>A full-screen program took over the pane, so nothing more is appended.</summary>
    AlternateScreen = 6,
}

/// <summary>Describes how a wait on a pane's output ended.</summary>
/// <param name="Outcome">How the wait ended.</param>
/// <param name="Pattern">The pattern that matched, when one did.</param>
/// <param name="Elapsed">How long the wait ran.</param>
public sealed record PaneWaitResult(PaneWaitOutcome Outcome, string? Pattern, TimeSpan Elapsed)
{
    /// <summary>Gets whether the awaited text appeared, before or during the wait.</summary>
    public bool Found => Outcome is PaneWaitOutcome.Matched or PaneWaitOutcome.PresentAtEntry;
}
