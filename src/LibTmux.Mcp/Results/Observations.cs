namespace LibTmux.Mcp;

/// <summary>A pane's content and its screen state, read together.</summary>
/// <param name="Pane">What the pane is and what runs in it.</param>
/// <param name="Content">What is on the screen, within the budget.</param>
/// <param name="CursorX">The cursor's column, zero-based.</param>
/// <param name="CursorY">The cursor's row within the visible screen, zero-based.</param>
/// <param name="AlternateScreen">
/// Whether a full-screen program owns the pane. When it does, scrollback holds
/// whatever was on screen before that program started, not its output.
/// </param>
/// <remarks>
/// One call rather than a capture followed by an info read: the two would be
/// separated by however long the model took to ask again, and a pane can move
/// in between, which is how a cursor ends up described against the wrong text.
/// </remarks>
public sealed record PaneSnapshot(
    PaneInfo Pane,
    BoundedText Content,
    int? CursorX,
    int? CursorY,
    bool AlternateScreen);

/// <summary>What a pane was showing.</summary>
/// <param name="PaneId">The pane that was read.</param>
/// <param name="Content">The text, within the budget.</param>
public sealed record CaptureResult(string PaneId, BoundedText Content);

/// <summary>What a pane has printed since it was last read.</summary>
/// <param name="PaneId">The pane that was read.</param>
/// <param name="Content">The new text, within the budget.</param>
/// <param name="Cursor">
/// Where this read finished. Pass it to the next call to continue from here.
/// It is opaque; do not build one.
/// </param>
/// <param name="LinesMissed">
/// Whether scrollback dropped lines this read never saw. tmux frees the oldest
/// history once <c>history-limit</c> is reached, so a pane that printed more
/// than that between two reads loses the lines in between for good.
/// </param>
/// <param name="AnchorLost">
/// Whether the previous position could no longer be found, so the read started
/// from what is visible now rather than from where it left off.
/// </param>
/// <remarks>
/// This is the tool for watching a pane across turns. It answers only what is
/// new, so the tenth read of a busy pane costs what the first one did rather
/// than ten times as much.
/// </remarks>
public sealed record TailResult(
    string PaneId,
    BoundedText Content,
    string Cursor,
    bool LinesMissed,
    bool AnchorLost);

/// <summary>How a wait ended.</summary>
public enum WaitOutcome
{
    /// <summary>One of the patterns the caller asked for appeared.</summary>
    Matched = 0,

    /// <summary>The pane printed something, and the caller asked for any output.</summary>
    AnyOutput = 1,

    /// <summary>One of the caller's stop patterns appeared, so waiting was pointless.</summary>
    Stopped = 2,

    /// <summary>The time ran out before anything matched.</summary>
    Timeout = 3,

    /// <summary>The pane's program exited while the wait was running.</summary>
    PaneDied = 4,
}

/// <summary>What happened while waiting for a pane to print something.</summary>
/// <param name="PaneId">The pane that was watched.</param>
/// <param name="Outcome">How the wait ended.</param>
/// <param name="MatchedPattern">The pattern that ended it, when one did.</param>
/// <param name="Tail">The last lines of the pane when the wait ended.</param>
/// <param name="ElapsedSeconds">How long the wait ran.</param>
/// <param name="EffectiveTimeoutSeconds">
/// The timeout actually used. An over-large request is lowered to the server's
/// ceiling rather than refused, so read this instead of assuming the value
/// asked for was honoured.
/// </param>
public sealed record WaitResult(
    string PaneId,
    WaitOutcome Outcome,
    string? MatchedPattern,
    BoundedText Tail,
    double ElapsedSeconds,
    double EffectiveTimeoutSeconds);

/// <summary>What a command did.</summary>
/// <param name="PaneId">The pane it ran in.</param>
/// <param name="ExitStatus">
/// The shell's exit status, or null when the command did not finish in time.
/// </param>
/// <param name="TimedOut">
/// Whether waiting stopped before completion. The shell command may still be
/// running; inspect the pane and do not retry it.
/// </param>
/// <param name="Output">What the command printed, within the budget.</param>
/// <param name="ElapsedSeconds">How long it took.</param>
/// <param name="EffectiveTimeoutSeconds">The timeout actually used, after the server's ceiling.</param>
/// <param name="LinesMissed">Whether scrollback dropped output before it could be read.</param>
/// <param name="AnchorLost">Whether the pre-command output position could no longer be found.</param>
/// <remarks>
/// The command runs in a subshell, so a <c>cd</c> or an <c>export</c> in it
/// does not survive into the next call.
/// </remarks>
public sealed record RunResult(
    string PaneId,
    int? ExitStatus,
    bool TimedOut,
    BoundedText Output,
    double ElapsedSeconds,
    double EffectiveTimeoutSeconds,
    bool LinesMissed = false,
    bool AnchorLost = false);

/// <summary>One pane whose text matched a search.</summary>
/// <param name="PaneId">The pane that matched.</param>
/// <param name="WindowId">The window holding it.</param>
/// <param name="SessionId">The session holding that window.</param>
/// <param name="Matches">The matching lines, with the row each was found on.</param>
public sealed record PaneMatch(
    string PaneId,
    string WindowId,
    string SessionId,
    IReadOnlyList<MatchedLine> Matches);

/// <summary>One line that matched a search.</summary>
/// <param name="Row">
/// The row it was found on. Zero is the top of the visible screen; a negative
/// row is that many lines back into scrollback.
/// </param>
/// <param name="Text">The line.</param>
public sealed record MatchedLine(int Row, string Text);

/// <summary>What a search across panes found.</summary>
/// <param name="Pattern">The pattern that was searched for.</param>
/// <param name="PanesSearched">How many panes were read.</param>
/// <param name="Panes">The panes that matched, and what matched in them.</param>
/// <param name="Truncated">Whether the match limit stopped the search early.</param>
public sealed record SearchResult(
    string Pattern,
    int PanesSearched,
    IReadOnlyList<PaneMatch> Panes,
    bool Truncated);

/// <summary>What a tool that changed tmux did.</summary>
/// <param name="Changed">What tmux now holds that it did not before, in plain words.</param>
/// <param name="PaneId">The pane the action produced or acted on, when there is one.</param>
/// <param name="WindowId">The window the action produced or acted on, when there is one.</param>
/// <param name="SessionId">The session the action produced or acted on, when there is one.</param>
/// <remarks>
/// A mutating tool answers what it created rather than the whole hierarchy, so
/// the identifier needed for the next call is already in hand and no listing is
/// required to find it.
/// </remarks>
public sealed record ActionResult(
    string Changed,
    string? PaneId = null,
    string? WindowId = null,
    string? SessionId = null);

/// <summary>What pane input targeted after tmux synchronization was resolved.</summary>
/// <param name="Changed">What was sent, in plain words.</param>
/// <param name="PaneId">The pane named by the caller after active-pane resolution.</param>
/// <param name="TargetPaneIds">
/// The named source when its effective pane_synchronized value is 0. Only when
/// it is 1, every configured effective-on cohort member; membership does not
/// prove delivery.
/// </param>
public sealed record PaneInputResult(
    string Changed,
    string PaneId,
    IReadOnlyList<string> TargetPaneIds);
