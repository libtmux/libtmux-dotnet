namespace LibTmux;

/// <summary>Describes one <c>display-message</c> invocation.</summary>
public sealed record DisplayMessageRequest : ITmuxRequest<Server>
{
    private readonly string _message = "";
    private readonly TimeSpan? _delay;

    /// <summary>Gets the message, which tmux expands as a format.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public string Message
    {
        get => _message;
        init
        {
            ArgumentNullException.ThrowIfNull(value);

            _message = value;
        }
    }

    /// <summary>Gets whether the message is printed rather than shown.</summary>
    public bool ReturnText { get; init; }

    /// <summary>Gets the format string used in place of the message.</summary>
    public string? Format { get; init; }

    /// <summary>Gets whether every format variable is listed.</summary>
    public bool AllFormats { get; init; }

    /// <summary>Gets whether format expansion is reported.</summary>
    public bool Verbose { get; init; }

    /// <summary>Gets whether the message is sent without format expansion.</summary>
    /// <remarks>tmux gained this in 3.4; older servers always expand.</remarks>
    public bool NoExpand { get; init; }

    /// <summary>Gets the client to show the message on.</summary>
    public string? TargetClient { get; init; }

    /// <summary>Gets how long the message stays up.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan? Delay
    {
        get => _delay;
        init
        {
            if (value is TimeSpan window && window < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Delay),
                    window,
                    "A delay cannot be negative.");
            }

            _delay = value;
        }
    }

    /// <summary>Gets whether the message is delivered as a notification.</summary>
    public bool Notify { get; init; }

    /// <summary>Gets whether the pane is redrawn while the message is shown.</summary>
    /// <remarks>Only a pane can honour this; a window-scoped call rejects it.</remarks>
    public bool UpdatePane { get; init; }

    /// <summary>Returns a message request as one tmux command.</summary>
    /// <param name="server">The server the message is shown on.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// This takes the server because two of the flags depend on which tmux is
    /// answering: literal expansion arrived in 3.4, and 3.2a refuses the
    /// target-client flag even for a client that is really attached.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="server" /> is null.</exception>
    public TmuxCommand ToCommand(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return TmuxChaining.Command([.. server.BuildDisplayMessageArguments(this)]);
    }
}
