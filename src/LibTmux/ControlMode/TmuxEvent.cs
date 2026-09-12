namespace LibTmux;

/// <summary>One thing a tmux control client reported without being asked.</summary>
/// <remarks>
/// tmux names a notification and then a version-dependent list of words after
/// it. Modelling every name as its own type would freeze a set that moves
/// between 3.2a and 3.7b, so the two a caller reacts to are typed and the rest
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
/// <param name="Count">The events discarded since the previous loss report.</param>
/// <param name="TotalDropped">The events discarded over this control client's lifetime.</param>
/// <remarks>
/// LibTmux synthesizes this event before the next retained event. Command
/// replies use a separate queue and are never discarded by this buffer.
/// </remarks>
public sealed record TmuxEventsDroppedEvent(long Count, long TotalDropped) : TmuxEvent;

/// <summary>The control client ended.</summary>
/// <param name="Reason">
/// Why tmux said it ended, when it said anything. It is silent for an ordinary
/// exit and names a reason when the server went away underneath the client.
/// </param>
/// <remarks>
/// This is always the last event, and the event stream completes after it.
/// </remarks>
public sealed record TmuxExitEvent(string? Reason) : TmuxEvent;
