namespace LibTmux;

/// <summary>Names a layout change that needs no layout string.</summary>
public enum SelectLayoutMode
{
    /// <summary>Spread the panes out evenly.</summary>
    Spread = 0,

    /// <summary>Move to the next layout.</summary>
    Next = 1,

    /// <summary>Move to the previous layout.</summary>
    Previous = 2,
}

/// <summary>Describes one <c>select-layout</c> invocation.</summary>
public sealed record SelectLayoutRequest
{
    private readonly SelectLayoutMode? _mode;

    /// <summary>Gets the named layout, or a layout string tmux dumped.</summary>
    public string? Layout { get; init; }

    /// <summary>Gets the layout change that needs no name.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined mode.</exception>
    public SelectLayoutMode? Mode
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
}
