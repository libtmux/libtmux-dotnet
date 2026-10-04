namespace LibTmux;

/// <summary>Describes how a command run in a pane ended.</summary>
/// <param name="ExitStatus">The command's exit status, or null when it had not finished.</param>
/// <param name="TimedOut">Whether the time allowed ran out first; the command may still be running.</param>
/// <param name="Output">The lines the command printed.</param>
/// <param name="Elapsed">How long the command ran, or how long it was waited for.</param>
/// <param name="Started">Whether the pane's shell ran the command at all.</param>
/// <param name="LinesMissed">
/// Whether scrollback dropped output before it was read; <paramref name="Output" />
/// then holds only what the pane still showed.
/// </param>
public sealed record PaneRunResult(
    int? ExitStatus,
    bool TimedOut,
    IReadOnlyList<string> Output,
    TimeSpan Elapsed,
    bool Started,
    bool LinesMissed)
{
    /// <summary>Gets whether the command finished with exit status 0.</summary>
    public bool Succeeded => ExitStatus == 0;
}
