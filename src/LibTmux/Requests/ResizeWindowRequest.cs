namespace LibTmux;

/// <summary>Names how a window is resized against its clients.</summary>
public enum WindowResizeMode
{
    /// <summary>Size the window to its largest client.</summary>
    Expand = 0,

    /// <summary>Size the window to its smallest client.</summary>
    Shrink = 1,
}

/// <summary>Describes one <c>resize-window</c> invocation.</summary>
/// <remarks>
/// tmux applies a mode after a direction or an explicit size and silently
/// discards the loser, so the request refuses the ambiguity instead.
/// </remarks>
public sealed record ResizeWindowRequest
{
    private readonly ResizeDirection? _direction;
    private readonly int? _adjustment;
    private readonly int? _width;
    private readonly int? _height;
    private readonly WindowResizeMode? _mode;

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
            ThrowIfNotPositive(value, nameof(Adjustment));

            _adjustment = value;
        }
    }

    /// <summary>Gets the explicit width.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Width
    {
        get => _width;
        init
        {
            ThrowIfNotPositive(value, nameof(Width));

            _width = value;
        }
    }

    /// <summary>Gets the explicit height.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Height
    {
        get => _height;
        init
        {
            ThrowIfNotPositive(value, nameof(Height));

            _height = value;
        }
    }

    /// <summary>Gets the sizing to follow against the window's clients.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined mode.</exception>
    public WindowResizeMode? Mode
    {
        get => _mode;
        init
        {
            if (value is not null && !Enum.IsDefined(value.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(Mode));
            }

            _mode = value;
        }
    }

    /// <summary>Resolves the adjustment, refusing an instruction tmux would half-apply.</summary>
    /// <returns>The trailing adjustment, or null when no edge moves.</returns>
    /// <exception cref="ArgumentException">
    /// More than one of direction, explicit size, and mode is set, or a
    /// direction and an adjustment are not set together.
    /// </exception>
    /// <remarks>
    /// Initializers cannot check one property against another, so the rules
    /// are settled where the adjustment is derived. Every caller that sends a
    /// resize needs this value, so none can reach tmux having skipped them.
    /// </remarks>
    internal int? ResolveAdjustment()
    {
        bool hasSize = Width is not null || Height is not null;
        int primaries = (Direction is null ? 0 : 1) + (hasSize ? 1 : 0) + (Mode is null ? 0 : 1);
        if (primaries > 1)
        {
            throw new ArgumentException(
                "A resize moves an edge, sets a size, or follows the clients; not more than one.",
                nameof(Mode));
        }

        if (Direction is null && Adjustment is not null)
        {
            throw new ArgumentException(
                "An adjustment has no meaning without a direction to apply it to.",
                nameof(Adjustment));
        }

        if (Direction is not null && Adjustment is null)
        {
            throw new ArgumentException(
                "A direction needs an adjustment to move by.",
                nameof(Adjustment));
        }

        return Adjustment;
    }

    private static void ThrowIfNotPositive(int? value, string parameterName)
    {
        if (value is int cells && cells <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, cells, "Cells must be positive.");
        }
    }
}
