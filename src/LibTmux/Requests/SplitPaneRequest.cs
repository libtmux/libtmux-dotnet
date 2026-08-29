using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Describes one <c>split-window</c> invocation.</summary>
public sealed record SplitPaneRequest
{
    /// <summary>Initializes a pane-split request.</summary>
    /// <param name="target">The pane to split, or null for the active one.</param>
    /// <param name="startDirectory">The working directory for the new pane.</param>
    /// <param name="attach">Whether the new pane becomes active.</param>
    /// <param name="direction">Where the new pane goes.</param>
    /// <param name="fullWindow">Whether the split spans the whole window.</param>
    /// <param name="zoom">Whether the new pane is zoomed.</param>
    /// <param name="command">The command the new pane runs.</param>
    /// <param name="size">An explicit size in cells.</param>
    /// <param name="percentage">A size as a percentage of the window.</param>
    /// <param name="environment">Environment entries set on the new pane.</param>
    /// <param name="empty">Whether the pane starts with no command.</param>
    /// <param name="style">The pane style.</param>
    /// <param name="activeBorderStyle">The border style while the pane is active.</param>
    /// <param name="inactiveBorderStyle">The border style while it is not.</param>
    /// <param name="message">A message shown in the pane.</param>
    /// <param name="keepOpen">Whether the pane stays after its command exits.</param>
    /// <exception cref="ArgumentException">
    /// Both <paramref name="size" /> and <paramref name="percentage" /> are set.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="percentage" /> is outside 1 to 100.
    /// </exception>
    public SplitPaneRequest(
        string? target = null,
        string? startDirectory = null,
        bool attach = false,
        PaneDirection? direction = null,
        bool fullWindow = false,
        bool zoom = false,
        string? command = null,
        string? size = null,
        int? percentage = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool empty = false,
        string? style = null,
        string? activeBorderStyle = null,
        string? inactiveBorderStyle = null,
        string? message = null,
        bool keepOpen = false)
    {
        if (size is not null && percentage is not null)
        {
            throw new ArgumentException(
                "A split is sized in cells or as a percentage, not both.",
                nameof(percentage));
        }

        if (percentage is int share && share is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentage),
                share,
                "A percentage runs from 1 to 100.");
        }

        Target = target;
        StartDirectory = startDirectory;
        Attach = attach;
        Direction = direction;
        FullWindow = fullWindow;
        Zoom = zoom;
        Command = command;
        Size = size;
        Percentage = percentage;
        // The request is read again at dispatch, so a caller that kept the
        // dictionary could otherwise change the argv after constructing it.
        Environment = environment is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(environment, StringComparer.Ordinal));
        Empty = empty;
        Style = style;
        ActiveBorderStyle = activeBorderStyle;
        InactiveBorderStyle = inactiveBorderStyle;
        Message = message;
        KeepOpen = keepOpen;
    }

    /// <summary>Gets the pane to split, or null for the active one.</summary>
    public string? Target { get; }

    /// <summary>Gets the working directory for the new pane.</summary>
    /// <remarks>
    /// tmux expands it as a format before it changes directory, so a <c>#</c>
    /// in it does not survive verbatim.
    /// </remarks>
    public string? StartDirectory { get; }

    /// <summary>Gets whether the new pane becomes active.</summary>
    public bool Attach { get; }

    /// <summary>Gets where the new pane goes.</summary>
    public PaneDirection? Direction { get; }

    /// <summary>Gets whether the split spans the whole window.</summary>
    public bool FullWindow { get; }

    /// <summary>Gets whether the new pane is zoomed.</summary>
    public bool Zoom { get; }

    /// <summary>Gets the command the new pane runs.</summary>
    public string? Command { get; }

    /// <summary>Gets the explicit size in cells.</summary>
    public string? Size { get; }

    /// <summary>Gets the size as a percentage of the window.</summary>
    public int? Percentage { get; }

    /// <summary>Gets the environment entries set on the new pane.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; }

    /// <summary>Gets whether the pane starts with no command.</summary>
    public bool Empty { get; }

    /// <summary>Gets the pane style.</summary>
    public string? Style { get; }

    /// <summary>Gets the border style while the pane is active.</summary>
    public string? ActiveBorderStyle { get; }

    /// <summary>Gets the border style while the pane is not active.</summary>
    public string? InactiveBorderStyle { get; }

    /// <summary>Gets the message shown in the pane.</summary>
    public string? Message { get; }

    /// <summary>Gets whether the pane stays after its command exits.</summary>
    public bool KeepOpen { get; }
}
