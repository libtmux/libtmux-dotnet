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
    private readonly ResizeDirection? _direction;
    private readonly int? _adjustment;
    private readonly string? _width;
    private readonly string? _height;

    /// <summary>Gets the edge to move.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined direction.</exception>
    public ResizeDirection? Direction
    {
        get => _direction;
        init
        {
            if (value is not null && !Enum.IsDefined(value.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(Direction));
            }

            _direction = value;
        }
    }

    /// <summary>Gets how many cells to move the edge by.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Adjustment
    {
        get => _adjustment;
        init
        {
            if (value is int cells && cells <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Adjustment),
                    cells,
                    "Cells must be positive.");
            }

            _adjustment = value;
        }
    }

    /// <summary>Gets the explicit width in cells or as a percentage.</summary>
    /// <exception cref="ArgumentException">The value is not a positive number of cells or a percentage.</exception>
    public string? Width
    {
        get => _width;
        init
        {
            ValidateExtent(value, nameof(Width));

            _width = value;
        }
    }

    /// <summary>Gets the explicit height in cells or as a percentage.</summary>
    /// <exception cref="ArgumentException">The value is not a positive number of cells or a percentage.</exception>
    public string? Height
    {
        get => _height;
        init
        {
            ValidateExtent(value, nameof(Height));

            _height = value;
        }
    }

    /// <summary>Gets whether the pane's zoom is toggled.</summary>
    public bool Zoom { get; init; }

    /// <summary>Gets whether the resize follows the mouse.</summary>
    public bool Mouse { get; init; }

    /// <summary>Gets whether lines below the cursor are trimmed.</summary>
    public bool TrimBelow { get; init; }

    /// <summary>Resolves the adjustment, refusing an instruction tmux would half-apply.</summary>
    /// <returns>The trailing adjustment, or null when no edge moves.</returns>
    /// <exception cref="ArgumentException">
    /// No sizing instruction is set, more than one is, or a direction and an
    /// adjustment are not set together.
    /// </exception>
    /// <remarks>
    /// Initializers cannot check one property against another, so the rules
    /// are settled where the adjustment is derived. Every caller that sends a
    /// resize needs this value, so none can reach tmux having skipped them.
    /// </remarks>
    internal int? ResolveAdjustment()
    {
        int modes = (Direction is null ? 0 : 1)
            + (Width is not null || Height is not null ? 1 : 0)
            + (Zoom ? 1 : 0)
            + (Mouse ? 1 : 0);
        if (modes != 1)
        {
            throw new ArgumentException(
                "A resize moves an edge, sets a size, toggles zoom, or follows the mouse; "
                + "exactly one.",
                nameof(Direction));
        }

        if (Direction is not null && Adjustment is null)
        {
            throw new ArgumentException(
                "A direction needs an adjustment to move by.",
                nameof(Adjustment));
        }

        if (Direction is null && Adjustment is not null)
        {
            throw new ArgumentException(
                "An adjustment has no meaning without a direction to apply it to.",
                nameof(Adjustment));
        }

        return Adjustment;
    }

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
