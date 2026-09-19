namespace LibTmux;

/// <summary>Describes one <c>send-keys</c> invocation.</summary>
public sealed record SendKeysRequest : ITmuxRequest<Pane>
{
    /// <summary>Gets the text or key names to send.</summary>
    public string? Text { get; init; }

    /// <summary>Gets whether Enter follows the text.</summary>
    /// <remarks>
    /// Enter is a second command rather than an appended key, because a literal
    /// send would otherwise type the five characters of its name.
    /// </remarks>
    public bool Enter { get; init; } = true;

    /// <summary>Gets whether the shell is asked not to record the line.</summary>
    /// <remarks>
    /// There is no tmux flag for this: the text is sent with a leading space,
    /// which most shells take as a signal to keep it out of history.
    /// </remarks>
    public bool SuppressHistory { get; init; }

    /// <summary>Gets whether the text is sent verbatim rather than as key names.</summary>
    public bool Literal { get; init; }

    /// <summary>Gets whether the pane's terminal state is reset first.</summary>
    public bool Reset { get; init; }

    /// <summary>Gets the copy-mode command to send instead of text.</summary>
    public string? CopyModeCommand { get; init; }

    /// <summary>Gets how many times the keys repeat.</summary>
    public int? Repeat { get; init; }

    /// <summary>Gets whether the text is expanded as a format.</summary>
    public bool ExpandFormats { get; init; }

    /// <summary>Gets whether key names are read as hexadecimal.</summary>
    public bool HexKeys { get; init; }

    /// <summary>Gets the client whose keys are sent.</summary>
    /// <remarks>tmux gained this in 3.4.</remarks>
    public string? TargetClient { get; init; }

    /// <summary>Gets whether the text names a key rather than a string.</summary>
    /// <remarks>tmux gained this in 3.4.</remarks>
    public bool KeyName { get; init; }

    /// <summary>Returns a key request as one tmux command for a pane.</summary>
    /// <param name="pane">The pane that receives them.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pane" /> is null.</exception>
    public TmuxCommand ToCommand(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        // The pane ID travels into the chain as plain text, so RequiredGeneration
        // pins it: after a restart, that ID could name a different pane.
        return TmuxChaining.Command([.. pane.BuildSendKeysArguments(this)]) with
        {
            RequiredGeneration = pane.Generation,
        };
    }
}
