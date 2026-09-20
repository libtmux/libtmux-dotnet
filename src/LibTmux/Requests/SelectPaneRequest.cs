namespace LibTmux;

/// <summary>Names which pane a selection moves to.</summary>
public enum PaneSelectDirection
{
    /// <summary>The pane above.</summary>
    Up = 0,

    /// <summary>The pane below.</summary>
    Down = 1,

    /// <summary>The pane to the left.</summary>
    Left = 2,

    /// <summary>The pane to the right.</summary>
    Right = 3,

    /// <summary>The pane that was last active.</summary>
    Last = 4,
}

/// <summary>Describes one <c>select-pane</c> invocation.</summary>
public sealed record SelectPaneRequest : ITmuxRequest<Pane>
{
    private readonly PaneSelectDirection? _direction;

    /// <summary>Gets which pane to move to.</summary>
    /// <remarks>
    /// <see cref="PaneSelectDirection.Last" /> and <see cref="Last" /> are two
    /// spellings of the same tmux flag, which is sent once either way.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined direction.</exception>
    public PaneSelectDirection? Direction
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

    /// <summary>Gets whether a zoomed pane stays zoomed.</summary>
    public bool KeepZoom { get; init; }

    /// <summary>Gets whether the pane is marked, unmarked, or left alone.</summary>
    /// <remarks>Null omits both flags, so tmux leaves the mark as it is.</remarks>
    public bool? Mark { get; init; }

    /// <summary>Gets whether input is enabled, disabled, or left alone.</summary>
    /// <remarks>Null omits both flags, so tmux leaves input as it is.</remarks>
    public bool? InputEnabled { get; init; }

    /// <summary>Gets whether the last active pane is selected.</summary>
    public bool Last { get; init; }

    /// <summary>Returns a pane-selection request as one tmux command.</summary>
    /// <param name="pane">The pane the selection is relative to.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pane" /> is null.</exception>
    public TmuxCommand ToCommand(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TmuxChaining.Command([.. pane.BuildSelectPaneArguments(this)]) with
        {
            RequiredGeneration = pane.Generation,
        };
    }
}
