using System.Text;

namespace LibTmux;

/// <summary>Describes a shell command to run in a pane and wait for.</summary>
public sealed record PaneRunRequest
{
    internal const int MaximumCommandBytes = 64 * 1024;
    internal static readonly TimeSpan MaximumTimeout = TimeSpan.FromDays(1);

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

    /// <summary>Gets the most recent rendered output lines to return.</summary>
    public int MaxOutputLines { get; init; } = 1000;

    /// <summary>Gets the maximum UTF-8 bytes of rendered output to return.</summary>
    public int MaxOutputBytes { get; init; } = 65_536;

    /// <summary>Validates the request without reading a pane or creating tmux resources.</summary>
    /// <exception cref="ArgumentException">The command is blank, contains NUL, or exceeds 65536 UTF-8 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout or output bounds are invalid.</exception>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Command);
        if (Command.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The command cannot contain NUL.", nameof(Command));
        }

        if (Command.Length > MaximumCommandBytes
            || Encoding.UTF8.GetByteCount(Command) > MaximumCommandBytes)
        {
            throw new ArgumentException(
                $"The command exceeds {MaximumCommandBytes} UTF-8 bytes. Run a script file instead.",
                nameof(Command));
        }

        if (Timeout <= TimeSpan.Zero || Timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Timeout), "The completion wait must be positive and at most one day.");
        }

        if (MaxOutputLines is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxOutputLines), "MaxOutputLines must be between 1 and 1000.");
        }

        if (MaxOutputBytes is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxOutputBytes), "MaxOutputBytes must be between 1 and 1048576.");
        }
    }
}
