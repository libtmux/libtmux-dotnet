namespace LibTmux;

/// <summary>Names how a wait on a pane's rendered output ended.</summary>
public enum PaneWaitOutcome
{
    /// <summary>New output matched a pattern.</summary>
    Matched = 0,

    /// <summary>A wanted pattern already matched when the wait began.</summary>
    PresentAtEntry = 1,

    /// <summary>The pane printed something, and no wanted pattern was given.</summary>
    AnyOutput = 2,

    /// <summary>A stop pattern matched, including at entry.</summary>
    Stopped = 3,

    /// <summary>The complete time budget ran out without a match.</summary>
    TimedOut = 4,

    /// <summary>The pane's original program exited or its pane closed.</summary>
    PaneExited = 5,

    /// <summary>An observation source could not continue across an alternate screen.</summary>
    AlternateScreen = 6,
}

/// <summary>Describes how a wait on a pane's rendered output ended.</summary>
/// <param name="Outcome">How the wait ended.</param>
/// <param name="Pattern">The original pattern spelling that matched, when one did.</param>
/// <param name="Elapsed">How long the wait ran.</param>
public sealed record PaneWaitResult(PaneWaitOutcome Outcome, string? Pattern, TimeSpan Elapsed)
{
    internal PaneWaitResult(
        PaneId paneId,
        PaneWaitOutcome outcome,
        string? pattern,
        IReadOnlyList<string> tail,
        TimeSpan elapsed,
        TimeSpan effectiveTimeout,
        long eventsDropped,
        bool pollingFallback,
        TimeSpan? pollingInterval,
        bool linesMissed,
        bool anchorLost,
        int omittedTailLines,
        int omittedTailBytes)
        : this(outcome, pattern, elapsed)
    {
        PaneId = paneId;
        Tail = Array.AsReadOnly([.. tail]);
        EffectiveTimeout = effectiveTimeout;
        EventsDropped = eventsDropped;
        PollingFallback = pollingFallback;
        PollingInterval = pollingInterval;
        LinesMissed = linesMissed;
        AnchorLost = anchorLost;
        OmittedTailLines = omittedTailLines;
        OmittedTailBytes = omittedTailBytes;
    }

    /// <summary>Gets whether the awaited text appeared, before or during the wait.</summary>
    public bool Found => Outcome is PaneWaitOutcome.Matched or PaneWaitOutcome.PresentAtEntry;

    /// <summary>Gets the watched pane's typed ID.</summary>
    public PaneId PaneId { get; }

    /// <summary>Gets an owned, bounded tail of rendered pane lines.</summary>
    public IReadOnlyList<string> Tail { get; } = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>Gets the requested time budget applied to this wait.</summary>
    public TimeSpan EffectiveTimeout { get; }

    /// <summary>Gets session events dropped during this wait.</summary>
    public long EventsDropped { get; }

    /// <summary>Gets whether this wait used explicitly permitted polling.</summary>
    public bool PollingFallback { get; }

    /// <summary>Gets the timed-read interval when fallback was used.</summary>
    public TimeSpan? PollingInterval { get; }

    /// <summary>Gets whether scrollback or event loss may have hidden lines.</summary>
    public bool LinesMissed { get; }

    /// <summary>Gets whether the previous grid anchor was lost.</summary>
    public bool AnchorLost { get; }

    /// <summary>Gets how many complete tail lines were omitted by limits.</summary>
    public int OmittedTailLines { get; }

    /// <summary>Gets how many UTF-8 tail bytes were omitted by limits.</summary>
    public int OmittedTailBytes { get; }
}
