namespace LibTmux;

/// <summary>Describes one <c>copy-mode</c> invocation.</summary>
public sealed record CopyModeRequest
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
}
