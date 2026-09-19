namespace LibTmux;

/// <summary>Describes one <c>move-pane</c> or <c>join-pane</c> invocation.</summary>
public sealed record MovePaneRequest
{
    private readonly PaneDirection _direction = PaneDirection.Below;

    /// <summary>Initializes a pane-move request.</summary>
    /// <param name="target">The pane or window to move against.</param>
    /// <exception cref="ArgumentException"><paramref name="target" /> is blank.</exception>
    public MovePaneRequest(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        Target = target;
    }

    /// <summary>Gets the pane or window to move against.</summary>
    public string Target { get; }

    /// <summary>Gets which side of the target the pane lands on.</summary>
    /// <remarks>
    /// Only the axis comes from the direction; landing before the target is
    /// <see cref="Before" />, which an above or left direction implies.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined direction.</exception>
    public PaneDirection Direction
    {
        get => _direction;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(Direction));
            }

            _direction = value;
        }
    }

    /// <summary>Gets the size in cells or as a percentage.</summary>
    /// <remarks>
    /// Sent as one sizing flag on every supported tmux, because the percentage
    /// flag is broken from 3.4 through 3.6.
    /// </remarks>
    public string? Size { get; init; }

    /// <summary>Gets whether the moved pane is left unselected.</summary>
    public bool Detach { get; init; } = true;

    /// <summary>Gets whether the split spans the whole window.</summary>
    public bool FullWindow { get; init; }

    /// <summary>Gets whether the pane lands before the target.</summary>
    public bool Before { get; init; }
}
