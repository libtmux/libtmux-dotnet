namespace LibTmux;

/// <summary>Describes one <c>copy-mode</c> invocation.</summary>
public sealed record CopyModeRequest : ITmuxRequest<Pane>
{
    /// <summary>Gets whether the pane scrolls up one page on entry.</summary>
    public bool ScrollUp { get; init; }

    /// <summary>Gets whether reaching the bottom leaves copy mode.</summary>
    public bool ExitOnBottom { get; init; }

    /// <summary>Gets whether the mode is entered for a mouse drag.</summary>
    /// <remarks>
    /// Without a real mouse event tmux accepts this and enters no mode.
    /// </remarks>
    public bool MouseDrag { get; init; }

    /// <summary>Gets whether copy mode is left instead of entered.</summary>
    public bool Cancel { get; init; }

    /// <summary>Gets whether the pane scrolls down one page on entry.</summary>
    /// <remarks>tmux gained this in 3.5.</remarks>
    public bool PageDown { get; init; }

    /// <summary>Gets the pane whose content is shown instead.</summary>
    public string? SourcePane { get; init; }

    /// <summary>Returns a copy-mode request as one tmux command.</summary>
    /// <param name="pane">The pane entering copy mode.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Paging down on entry arrived in tmux 3.5, so the pane decides whether
    /// the built command carries that flag.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="pane" /> is null.</exception>
    public TmuxCommand ToCommand(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TmuxChaining.Command([.. pane.BuildCopyModeArguments(this)]) with
        {
            RequiredGeneration = pane.Generation,
        };
    }
}
