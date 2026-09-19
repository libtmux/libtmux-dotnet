namespace LibTmux;

/// <summary>Names how a chooser orders its rows.</summary>
public enum ChooseTreeSort
{
    /// <summary>Order by index.</summary>
    Index = 0,

    /// <summary>Order by name.</summary>
    Name = 1,

    /// <summary>Order by activity time.</summary>
    /// <remarks>tmux dropped this in 3.7 and rejects it there.</remarks>
    Time = 2,

    /// <summary>Order by size.</summary>
    /// <remarks>Accepted on every supported tmux, but only sorts from 3.7.</remarks>
    Size = 3,
}

/// <summary>Describes one <c>choose-tree</c> invocation.</summary>
public sealed record ChooseTreeRequest
{
    private readonly ChooseTreeSort? _sort;

    /// <summary>Gets whether sessions start collapsed.</summary>
    public bool SessionsCollapsed { get; init; }

    /// <summary>Gets whether windows start collapsed.</summary>
    public bool WindowsCollapsed { get; init; }

    /// <summary>Gets the format each row renders with.</summary>
    public string? Format { get; init; }

    /// <summary>Gets the raw tmux filter limiting the rows.</summary>
    public UnsafeTmuxFilter? NativeFilter { get; init; }

    /// <summary>Gets how the rows are ordered.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined sort.</exception>
    public ChooseTreeSort? Sort
    {
        get => _sort;
        init
        {
            if (value is not null && !Enum.IsDefined(value.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(Sort));
            }

            _sort = value;
        }
    }

    /// <summary>Gets whether the order is reversed.</summary>
    public bool Reverse { get; init; }

    /// <summary>Gets whether the chooser pane is zoomed.</summary>
    public bool Zoom { get; init; }
}
