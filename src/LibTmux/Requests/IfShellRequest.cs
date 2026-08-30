namespace LibTmux;

/// <summary>Describes one <c>if-shell</c> invocation.</summary>
public sealed record IfShellRequest
{
    private readonly string[] _thenCommand;
    private readonly string[]? _elseCommand;

    /// <summary>Initializes a conditional command.</summary>
    /// <param name="shellCommand">The shell command whose success decides.</param>
    /// <param name="thenCommand">The tmux command run when it succeeds.</param>
    /// <param name="elseCommand">The tmux command run when it fails, when any.</param>
    /// <param name="background">Whether tmux runs the shell command without waiting.</param>
    /// <param name="targetPane">The pane the commands run against.</param>
    public IfShellRequest(
        string shellCommand,
        IReadOnlyList<string> thenCommand,
        IReadOnlyList<string>? elseCommand = null,
        bool background = false,
        string? targetPane = null)
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
        _elseCommand = elseCommand is null ? null : [.. elseCommand];
        Background = background;
        TargetPane = targetPane;
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
    public IReadOnlyList<string>? ElseCommand => _elseCommand;

    /// <summary>Gets whether tmux runs the shell command without waiting.</summary>
    public bool Background { get; }

    /// <summary>Gets the pane the commands run against.</summary>
    public string? TargetPane { get; }
}
