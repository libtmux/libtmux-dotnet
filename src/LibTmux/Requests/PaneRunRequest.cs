namespace LibTmux;

/// <summary>Describes a shell command to run in a pane and wait for.</summary>
public sealed record PaneRunRequest
{
    /// <summary>Initializes a run request.</summary>
    /// <param name="command">The shell command, run in a subshell of the pane's shell.</param>
    /// <exception cref="ArgumentException"><paramref name="command" /> is blank.</exception>
    public PaneRunRequest(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        Command = command;
    }

    /// <summary>Gets the shell command.</summary>
    public string Command { get; }

    /// <summary>Gets how long to wait for the command to finish.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets whether the line the shell reads starts with a space, which many shells keep out of history.</summary>
    public bool KeepOutOfHistory { get; init; } = true;
}
