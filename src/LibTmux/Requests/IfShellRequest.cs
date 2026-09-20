namespace LibTmux;

/// <summary>Describes one <c>if-shell</c> invocation.</summary>
public sealed record IfShellRequest : ITmuxRequest<Server>
{
    private readonly string[] _thenCommand;
    private readonly string[]? _elseCommand;

    /// <summary>Initializes a conditional command.</summary>
    /// <param name="shellCommand">The shell command whose success decides.</param>
    /// <param name="thenCommand">The tmux command run when it succeeds.</param>
    public IfShellRequest(
        string shellCommand,
        IReadOnlyList<string> thenCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shellCommand);
        ArgumentNullException.ThrowIfNull(thenCommand);
        if (thenCommand.Count == 0)
        {
            throw new ArgumentException(
                "A conditional needs a command to run.",
                nameof(thenCommand));
        }

        ShellCommand = shellCommand;
        _thenCommand = [.. thenCommand];
    }

    /// <summary>Gets the shell command whose success decides.</summary>
    /// <remarks>
    /// tmux expands it as a format before running it, so a <c>#</c> in it does
    /// not survive verbatim.
    /// </remarks>
    public string ShellCommand { get; }

    /// <summary>Gets the tmux command run when it succeeds.</summary>
    public IReadOnlyList<string> ThenCommand => _thenCommand;

    /// <summary>Gets the tmux command run when it fails, when any.</summary>
    public IReadOnlyList<string>? ElseCommand
    {
        get => _elseCommand;
        init => _elseCommand = value is null ? null : [.. value];
    }

    /// <summary>Gets whether tmux runs the shell command without waiting.</summary>
    public bool Background { get; init; }

    /// <summary>Gets the pane the commands run against.</summary>
    public string? TargetPane { get; init; }

    /// <summary>Returns a conditional request as one tmux command.</summary>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    public TmuxCommand ToCommand() =>
        TmuxChaining.Command([.. Server.BuildIfShellArguments(this)]);

    /// <inheritdoc />
    TmuxCommand ITmuxRequest<Server>.ToCommand(Server target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ToCommand();
    }
}
