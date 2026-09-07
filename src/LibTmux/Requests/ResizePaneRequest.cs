namespace LibTmux;

/// <summary>Describes one <c>resize-pane</c> invocation.</summary>
/// <remarks>
/// tmux accepts several sizing instructions at once and silently applies only
/// some of them, so the request refuses the ambiguity instead. That makes
/// trimming below the cursor unrepresentable on its own; it rides alongside a
/// real resize.
/// </remarks>
public sealed record ResizePaneRequest
{
    /// <summary>Initializes a pane-resize request.</summary>
    /// <param name="direction">The edge to move.</param>
    /// <param name="adjustment">How many cells to move it by.</param>
    /// <param name="width">An explicit width in cells or a percentage.</param>
    /// <param name="height">An explicit height in cells or a percentage.</param>
    /// <param name="zoom">Whether the pane's zoom is toggled.</param>
    /// <param name="mouse">Whether the resize follows the mouse.</param>
    /// <param name="trimBelow">Whether lines below the cursor are trimmed.</param>
    /// <exception cref="ArgumentException">
    /// No sizing instruction is given, more than one is, or a direction has no
    /// adjustment.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A supplied adjustment is not positive.
    /// </exception>
    public ResizePaneRequest(
        ResizeDirection? direction = null,
        int? adjustment = null,
        string? width = null,
        string? height = null,
        bool zoom = false,
        bool mouse = false,
        bool trimBelow = false)
    {
        bool hasSize = width is not null || height is not null;
        int modes = (direction is null ? 0 : 1)
            + (hasSize ? 1 : 0)
            + (zoom ? 1 : 0)
            + (mouse ? 1 : 0);
        if (modes != 1)
        {
            throw new ArgumentException(
                "A resize moves an edge, sets a size, toggles zoom, or follows the mouse; "
                + "exactly one.",
                nameof(direction));
        }

        if (direction is not null && adjustment is null)
        {
            throw new ArgumentException(
                "A direction needs an adjustment to move by.",
                nameof(adjustment));
        }

        if (direction is null && adjustment is not null)
        {
            throw new ArgumentException(
                "An adjustment has no meaning without a direction to apply it to.",
                nameof(adjustment));
        }

        if (direction is not null && !Enum.IsDefined(direction.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (adjustment is int cells && cells <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(adjustment),
                cells,
                "Cells must be positive.");
        }

        ValidateExtent(width, nameof(width));
        ValidateExtent(height, nameof(height));

        Direction = direction;
        Adjustment = adjustment;
        Width = width;
        Height = height;
        Zoom = zoom;
        Mouse = mouse;
        TrimBelow = trimBelow;
    }

    /// <summary>Gets the edge to move.</summary>
    public ResizeDirection? Direction { get; }

    /// <summary>Gets how many cells to move the edge by.</summary>
    public int? Adjustment { get; }

    /// <summary>Gets the explicit width in cells or as a percentage.</summary>
    public string? Width { get; }

    /// <summary>Gets the explicit height in cells or as a percentage.</summary>
    public string? Height { get; }

    /// <summary>Gets whether the pane's zoom is toggled.</summary>
    public bool Zoom { get; }

    /// <summary>Gets whether the resize follows the mouse.</summary>
    public bool Mouse { get; }

    /// <summary>Gets whether lines below the cursor are trimmed.</summary>
    public bool TrimBelow { get; }

    private static void ValidateExtent(string? value, string parameterName)
    {
        if (value is null)
        {
            return;
        }

        string digits = value.EndsWith('%') ? value[..^1] : value;

        // Zero is rejected with the negatives it already refused: tmux coerces
        // it to one rather than honouring it, so accepting it would agree to a
        // size the caller did not ask for.
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)
            || digits.TrimStart('0').Length == 0)
        {
            throw new ArgumentException(
                "An extent is a positive number of cells or a percentage.",
                parameterName);
        }
    }
}
