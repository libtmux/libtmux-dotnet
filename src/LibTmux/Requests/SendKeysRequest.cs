namespace LibTmux;

/// <summary>Describes keys and an optional following Enter for a pane.</summary>
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

    /// <summary>Returns a key request that needs only one tmux command.</summary>
    /// <param name="pane">The pane that receives them.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pane" /> is null.</exception>
    /// <exception cref="ArgumentException">The request sends nothing or needs separate text and Enter commands.</exception>
    public TmuxCommand ToCommand(Pane pane)
    {
        IReadOnlyList<TmuxCommand> commands = ToCommands(pane);
        if (commands.Count != 1)
        {
            throw new ArgumentException(
                "The request sends text and Enter as separate commands. Use ToCommands instead.");
        }

        return commands[0];
    }

    /// <summary>Returns every command the key request sends, in order.</summary>
    /// <param name="pane">The pane that receives the keys.</param>
    /// <returns>The text command followed by Enter when requested.</returns>
    /// <remarks>Enter uses a separate command so literal mode types the text and then presses the key.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="pane" /> is null.</exception>
    /// <exception cref="ArgumentException">The request sends nothing or its text contains NUL.</exception>
    public IReadOnlyList<TmuxCommand> ToCommands(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return
        [
            .. pane.BuildSendKeysCommands(this).Select(arguments => TmuxChaining.Command([.. arguments]) with
            {
                RequiredGeneration = pane.Generation,
            }),
        ];
    }
}
