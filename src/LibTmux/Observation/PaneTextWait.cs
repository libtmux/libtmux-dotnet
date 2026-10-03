using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>How a rendered-pane text wait ended.</summary>
public enum PaneTextWaitOutcome
{
    /// <summary>A wanted pattern was already visible when observation began.</summary>
    PresentAtEntry,

    /// <summary>A wanted pattern appeared after the initial read.</summary>
    Matched,

    /// <summary>A stop pattern appeared before a wanted pattern.</summary>
    Stopped,

    /// <summary>The pane printed text and no wanted pattern was specified.</summary>
    AnyOutput,

    /// <summary>No match appeared within the effective wait budget.</summary>
    TimedOut,

    /// <summary>The watched pane or its original process ended.</summary>
    PaneDied,
}

/// <summary>Configures a wait for text rendered in a pane.</summary>
public sealed record PaneTextWaitRequest
{
    internal const int MaximumPatterns = 32;
    private IReadOnlyList<string>? _patterns;
    private IReadOnlyList<string>? _stopPatterns;

    /// <summary>Regular expressions to wait for, or null for any new output.</summary>
    public IReadOnlyList<string>? Patterns
    {
        get => _patterns;
        init => _patterns = OwnPatterns(value, nameof(Patterns));
    }

    /// <summary>Stop patterns checked before wanted patterns, including at entry.</summary>
    public IReadOnlyList<string>? StopPatterns
    {
        get => _stopPatterns;
        init => _stopPatterns = OwnPatterns(value, nameof(StopPatterns));
    }

    /// <summary>Gets whether pattern matching ignores case.</summary>
    public bool IgnoreCase { get; init; } = true;

    /// <summary>Gets whether patterns are matched as literal strings.</summary>
    public bool SimpleMatch { get; init; }

    /// <summary>Gets the requested wait duration.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the maximum number of recent rendered lines returned.</summary>
    public int TailLines { get; init; } = 20;

    /// <summary>Gets the maximum UTF-8 bytes returned in the rendered tail.</summary>
    public int MaxOutputBytes { get; init; } = 65_536;

    /// <summary>Validates local limits and compiles patterns before pane I/O.</summary>
    public void Validate() => PaneTextWaitEngine.ValidateRequest(this);

    private static ReadOnlyCollection<string>? OwnPatterns(
        IReadOnlyList<string>? values,
        string property)
    {
        if (values is null)
        {
            return null;
        }

        if (values.Count > MaximumPatterns)
        {
            throw new ArgumentException(
                $"A pane wait accepts at most {MaximumPatterns} patterns in one list.",
                property);
        }

        string[] owned = new string[values.Count];
        for (int index = 0; index < owned.Length; index++)
        {
            owned[index] = values[index];
        }

        return Array.AsReadOnly(owned);
    }
}

/// <summary>Reports the result of a rendered-pane text wait.</summary>
public sealed class PaneTextWaitResult
{
    internal PaneTextWaitResult(
        PaneId paneId,
        PaneTextWaitOutcome outcome,
        string? matchedPattern,
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
    {
        PaneId = paneId;
        Outcome = outcome;
        MatchedPattern = matchedPattern;
        Tail = Array.AsReadOnly([.. tail]);
        Elapsed = elapsed;
        EffectiveTimeout = effectiveTimeout;
        EventsDropped = eventsDropped;
        PollingFallback = pollingFallback;
        PollingInterval = pollingInterval;
        LinesMissed = linesMissed;
        AnchorLost = anchorLost;
        OmittedTailLines = omittedTailLines;
        OmittedTailBytes = omittedTailBytes;
    }

    /// <summary>Gets the watched pane's typed ID.</summary>
    public PaneId PaneId { get; }

    /// <summary>Gets why the wait ended.</summary>
    public PaneTextWaitOutcome Outcome { get; }

    /// <summary>Gets the pattern that ended the wait, when one did.</summary>
    public string? MatchedPattern { get; }

    /// <summary>Gets an owned, read-only tail of rendered pane lines.</summary>
    public IReadOnlyList<string> Tail { get; }

    /// <summary>Gets the elapsed duration.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Gets the timeout applied after the library ceiling.</summary>
    public TimeSpan EffectiveTimeout { get; }

    /// <summary>Gets session events dropped during this wait.</summary>
    public long EventsDropped { get; }

    /// <summary>Gets whether explicitly enabled polling was used.</summary>
    public bool PollingFallback { get; }

    /// <summary>Gets the polling interval when fallback was used.</summary>
    public TimeSpan? PollingInterval { get; }

    /// <summary>Gets whether scrollback may have lost unread lines.</summary>
    public bool LinesMissed { get; }

    /// <summary>Gets whether the previous grid anchor could not be recovered.</summary>
    public bool AnchorLost { get; }

    /// <summary>Gets how many whole tail lines were omitted by output limits.</summary>
    public int OmittedTailLines { get; }

    /// <summary>Gets how many UTF-8 tail bytes were omitted by output limits.</summary>
    public int OmittedTailBytes { get; }
}
