namespace LibTmux;

/// <summary>Names which neighbouring pane a swap uses.</summary>
public enum PaneSwapDirection
{
    /// <summary>The pane above.</summary>
    Up = 0,

    /// <summary>The pane below.</summary>
    Down = 1,
}

/// <summary>Describes one <c>swap-pane</c> invocation.</summary>
/// <remarks>
/// tmux replaces a named source with the neighbour whenever a direction is
/// given, so sending both would quietly drop the name.
/// </remarks>
public sealed record SwapPaneRequest : ITmuxRequest<Pane>
{
    private readonly PaneSwapDirection? _direction;

    /// <summary>Gets the pane to swap with.</summary>
    public string? Target { get; init; }

    /// <summary>Gets the neighbour to swap with instead.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined direction.</exception>
    public PaneSwapDirection? Direction
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

    /// <summary>Gets whether the swapped pane is left unselected.</summary>
    public bool Detach { get; init; }

    /// <summary>Gets whether a zoomed pane stays zoomed.</summary>
    public bool KeepZoom { get; init; }

    /// <summary>Resolves the named source, refusing a request that names two or none.</summary>
    /// <returns>The <c>-s</c> argument, or null when a direction is used instead.</returns>
    /// <exception cref="ArgumentException">
    /// Neither a target nor a direction is set, or both are.
    /// </exception>
    /// <remarks>
    /// Initializers cannot check one property against another, so the pairing
    /// is settled where the source is derived. Every caller that sends a swap
    /// needs this value, so none can reach tmux having skipped the check.
    /// </remarks>
    internal string? ResolveSource() =>
        (Target is null) == (Direction is null)
            ? throw new ArgumentException(
                "A swap names a pane or a direction, not both and not neither.",
                nameof(Target))
            : Target;

    /// <summary>Returns a pane-swap request as one tmux command.</summary>
    /// <param name="pane">The pane being swapped.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pane" /> is null.</exception>
    public TmuxCommand ToCommand(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TmuxChaining.Command([.. pane.BuildSwapPaneArguments(this)]) with
        {
            RequiredGeneration = pane.Generation,
        };
    }
}
