using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Describes one <c>new-pane</c> invocation.</summary>
/// <remarks>
/// <c>new-pane</c> arrived in tmux 3.7. Unlike a split it places a floating
/// pane, so it carries a position as well as a size.
/// </remarks>
public sealed record NewPaneRequest
{
    /// <summary>Initializes a pane-creation request.</summary>
    /// <param name="target">The window or pane to place against.</param>
    /// <param name="startDirectory">The working directory for the new pane.</param>
    /// <param name="attach">Whether the new pane becomes active.</param>
    /// <param name="command">The command the new pane runs.</param>
    /// <param name="environment">Environment entries set on the new pane.</param>
    /// <param name="width">The pane width in cells.</param>
    /// <param name="height">The pane height in cells.</param>
    /// <param name="x">The column to place the pane at.</param>
    /// <param name="y">The row to place the pane at.</param>
    /// <param name="zoom">Whether the new pane is zoomed.</param>
    /// <param name="empty">Whether the pane starts with no command.</param>
    /// <param name="style">The pane style.</param>
    /// <param name="activeBorderStyle">The border style while the pane is active.</param>
    /// <param name="inactiveBorderStyle">The border style while it is not.</param>
    /// <param name="message">A message shown in the pane.</param>
    /// <param name="keepOpen">Whether the pane stays after its command exits.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A supplied width or height is not positive, or a position is negative.
    /// </exception>
    public NewPaneRequest(
        string? target = null,
        string? startDirectory = null,
        bool attach = false,
        string? command = null,
        IReadOnlyDictionary<string, string>? environment = null,
        int? width = null,
        int? height = null,
        int? x = null,
        int? y = null,
        bool zoom = false,
        bool empty = false,
        string? style = null,
        string? activeBorderStyle = null,
        string? inactiveBorderStyle = null,
        string? message = null,
        bool keepOpen = false)
    {
        ThrowIfNotPositive(width, nameof(width));
        ThrowIfNotPositive(height, nameof(height));
        ThrowIfNegative(x, nameof(x));
        ThrowIfNegative(y, nameof(y));

        Target = target;
        StartDirectory = startDirectory;
        Attach = attach;
        Command = command;
        // The request is read again at dispatch, so a caller that kept the
        // dictionary could otherwise change the argv after constructing it.
        Environment = environment is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(environment, StringComparer.Ordinal));
        Width = width;
        Height = height;
        X = x;
        Y = y;
        Zoom = zoom;
        Empty = empty;
        Style = style;
        ActiveBorderStyle = activeBorderStyle;
        InactiveBorderStyle = inactiveBorderStyle;
        Message = message;
        KeepOpen = keepOpen;
    }

    /// <summary>Gets the window or pane to place against.</summary>
    public string? Target { get; }

    /// <summary>Gets the working directory for the new pane.</summary>
    /// <remarks>
    /// tmux expands it as a format before it changes directory, so a <c>#</c>
    /// in it does not survive verbatim.
    /// </remarks>
    public string? StartDirectory { get; }

    /// <summary>Gets whether the new pane becomes active.</summary>
    public bool Attach { get; }

    /// <summary>Gets the command the new pane runs.</summary>
    public string? Command { get; }

    /// <summary>Gets the environment entries set on the new pane.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; }

    /// <summary>Gets the pane width in cells.</summary>
    public int? Width { get; }

    /// <summary>Gets the pane height in cells.</summary>
    public int? Height { get; }

    /// <summary>Gets the column to place the pane at.</summary>
    public int? X { get; }

    /// <summary>Gets the row to place the pane at.</summary>
    public int? Y { get; }

    /// <summary>Gets whether the new pane is zoomed.</summary>
    public bool Zoom { get; }

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

    private static void ThrowIfNotPositive(int? value, string parameterName)
    {
        if (value is int cells && cells <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, cells, "Cells must be positive.");
        }
    }

    private static void ThrowIfNegative(int? value, string parameterName)
    {
        if (value is int position && position < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                position,
                "A position cannot be negative.");
        }
    }
}
