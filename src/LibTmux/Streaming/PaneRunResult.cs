namespace LibTmux;

/// <summary>Describes how a command run in a pane ended.</summary>
/// <param name="ExitStatus">The command's exit status, or null when it had not finished.</param>
/// <param name="TimedOut">Whether the time allowed ran out first; the command may still be running.</param>
/// <param name="Output">Bounded rendered lines from the command, not byte-exact stdout or stderr.</param>
/// <param name="Elapsed">How long the command ran, or how long it was waited for.</param>
/// <param name="Started">Whether the wrapper's begin marker was observed; false does not prove nonexecution.</param>
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
    /// <summary>Gets the pane that received the command.</summary>
    public PaneId PaneId { get; init; }

    /// <summary>Gets the completion-wait budget applied after dispatch.</summary>
    public TimeSpan EffectiveTimeout { get; init; }

    /// <summary>Gets whether the output cursor's previous grid anchor was lost.</summary>
    public bool AnchorLost { get; init; }

    /// <summary>Gets the number of whole rendered lines omitted by output limits.</summary>
    public int OmittedOutputLines { get; init; }

    /// <summary>Gets the number of UTF-8 output bytes omitted by output limits.</summary>
    public int OmittedOutputBytes { get; init; }

    /// <summary>Gets whether the command finished with exit status 0.</summary>
    public bool Succeeded => ExitStatus == 0;

    /// <summary>Gets whether the pane's program exited before the command reported its status.</summary>
    /// <remarks>
    /// The run ends within five seconds of the exit, sooner early in the run,
    /// rather than at its timeout.
    /// <see cref="ExitStatus" /> is then null, and <see cref="Output" /> holds
    /// what the pane still showed, including any line tmux writes for a dead
    /// pane, or nothing when tmux closed the pane.
    /// </remarks>
    public bool PaneExited { get; init; }
}
