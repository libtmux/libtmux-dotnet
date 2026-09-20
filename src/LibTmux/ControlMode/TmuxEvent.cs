namespace LibTmux;

/// <summary>One thing a tmux control client reported without being asked.</summary>
/// <remarks>
/// tmux names a notification and then a version-dependent list of words after
/// it. Modelling every name as its own type would freeze a set that moves
/// between 3.2a and 3.7b, so the ones a caller reacts to are typed and the rest
/// arrive named but unparsed.
/// </remarks>
public abstract record TmuxEvent;

/// <summary>Bytes a pane wrote.</summary>
/// <param name="PaneId">The pane that produced the output.</param>
/// <param name="Data">
/// The text, with tmux's escaping already decoded. It is a fragment of a
/// stream rather than a line: tmux sends whatever it has, so a single write by
/// the program in the pane can arrive split across events and one event can
/// carry several lines.
/// </param>
public sealed record TmuxOutputEvent(PaneId PaneId, string Data) : TmuxEvent;

/// <summary>A notification this library does not parse further.</summary>
/// <param name="Name">The notification name without its leading percent, such as <c>window-add</c>.</param>
/// <param name="Arguments">The words tmux printed after the name, unparsed.</param>
public sealed record TmuxNotificationEvent(
    string Name,
    IReadOnlyList<string> Arguments) : TmuxEvent;

/// <summary>Reports notifications discarded because the bounded event buffer was full.</summary>
/// <param name="Count">The discarded events represented by this report.</param>
/// <param name="TotalDropped">The cumulative events discarded by this client when the report was created.</param>
/// <remarks>
/// A full buffer discards the oldest output of the pane with the most queued
/// bytes under byte pressure, or events under count pressure. It discards
/// notifications only when no output is queued. An oversized event can produce
/// loss by itself. Command replies use a separate queue.
/// </remarks>
public sealed record TmuxEventsDroppedEvent(long Count, long TotalDropped) : TmuxEvent
{
    /// <summary>Gets whether this report proves that only pane output was lost.</summary>
    /// <remarks>A false value also covers uncertainty after the bounded loss history is compacted.</remarks>
    public bool OnlyOutput { get; init; }
}

/// <summary>tmux stopped sending a pane's output to this client.</summary>
/// <param name="PaneId">The paused pane.</param>
/// <remarks>
/// A control session pauses a pane whose output its full event buffer had to
/// discard, so a flooding pane stops costing either side anything. Output the
/// pane prints while paused is never sent; capture the pane to read its
/// screen. The session resumes the pane once the reader catches up, and a
/// reader that never reads leaves it paused. tmux's own <c>%pause</c>, from a
/// client with <c>pause-after</c> set, arrives as this event too.
/// </remarks>
public sealed record TmuxPanePausedEvent(PaneId PaneId) : TmuxEvent;

/// <summary>tmux resumed sending a pane's output to this client.</summary>
/// <param name="PaneId">The resumed pane.</param>
/// <remarks>Output resumes with what the pane prints next.</remarks>
public sealed record TmuxPaneContinuedEvent(PaneId PaneId) : TmuxEvent;

/// <summary>The control client ended.</summary>
/// <param name="Reason">
/// Why tmux said it ended, when it said anything. It is silent for an ordinary
/// exit. For an abnormal one tmux sometimes names a reason and sometimes does
/// not: a server another client killed, for one, sends a bare <c>%exit</c>
/// with none. A null <see cref="Reason" /> there is tmux's own silence, not
/// something this library failed to capture.
/// </param>
/// <remarks>
/// This is always the last event, and the event stream completes after it.
/// </remarks>
public sealed record TmuxExitEvent(string? Reason) : TmuxEvent;
