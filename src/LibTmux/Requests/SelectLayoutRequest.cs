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
public sealed record SelectLayoutRequest : ITmuxRequest<Window>
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

    /// <summary>Returns a layout request as one tmux command for a window.</summary>
    /// <param name="window">The window the layout applies to.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// This takes the window because a layout name is checked against the ones
    /// the running tmux knows, and an unrecognised name takes the whole server
    /// down on tmux 3.3a. Batching a layout must not skip that check.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="window" /> is null.</exception>
    /// <exception cref="TmuxWindowException">The layout is one tmux may not recognise.</exception>
    public TmuxCommand ToCommand(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return TmuxChaining.Command([.. window.BuildSelectLayoutArguments(this)]) with
        {
            RequiredGeneration = window.Generation,
            LayoutWindowId = Layout is null ? null : window.Id,
        };
    }
}
