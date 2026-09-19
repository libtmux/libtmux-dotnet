namespace LibTmux;

/// <summary>Describes one <c>run-shell</c> invocation.</summary>
public sealed record RunShellRequest : ITmuxRequest<Server>
{
    private readonly TimeSpan? _delay;
    private readonly string[]? _arguments;

    /// <summary>Initializes a shell command.</summary>
    /// <param name="command">The command to run.</param>
    public RunShellRequest(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        Command = command;
    }

    /// <summary>Gets the command to run.</summary>
    public string Command { get; }

    /// <summary>Gets arguments passed to it without a shell in between.</summary>
    public IReadOnlyList<string>? Arguments
    {
        get => _arguments;
        init => _arguments = value is null ? null : [.. value];
    }

    /// <summary>Gets whether tmux returns without waiting for it.</summary>
    public bool Background { get; init; }

    /// <summary>Gets how long tmux waits before starting it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan? Delay
    {
        get => _delay;
        init
        {
            if (value is TimeSpan span && span < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Delay),
                    value,
                    "A delay cannot run backwards.");
            }

            _delay = value;
        }
    }

    /// <summary>Gets whether the text is a tmux command rather than a shell one.</summary>
    public bool AsTmuxCommand { get; init; }

    /// <summary>Gets the pane the command runs against.</summary>
    public string? TargetPane { get; init; }

    /// <summary>Gets the directory it starts in.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets whether its error output is shown too.</summary>
    public bool ShowStandardError { get; init; }

    /// <summary>Returns a shell request as one tmux command.</summary>
    /// <param name="server">The server that runs it.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Three of this command's flags arrived at different tmux versions, so
    /// the server is what decides which of them the built command carries.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="server" /> is null.</exception>
    public TmuxCommand ToCommand(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return TmuxChaining.Command([.. server.BuildRunShellArguments(this)]);
    }
}
