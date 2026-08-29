namespace LibTmux;

/// <summary>A live tmux control client.</summary>
/// <remarks>
/// <para>
/// One-shot commands start a tmux client, run one thing, and exit. A control
/// session keeps one client for as long as it is held, which is what makes
/// tmux willing to report what happens while nobody asked: pane output, windows
/// appearing, sessions changing.
/// </para>
/// <para>
/// Disposing ends the client. Everything a caller reads comes through
/// <see cref="Events" />, which completes after the client exits.
/// </para>
/// </remarks>
public interface IControlModeSession : IAsyncDisposable
{
    /// <summary>Reads what tmux reports for as long as the client runs.</summary>
    /// <remarks>
    /// The sequence completes after <see cref="TmuxExitEvent" />. It may be
    /// enumerated once; a second enumeration reads only what has not already
    /// been taken. A slow reader receives <see cref="TmuxEventsDroppedEvent" />
    /// instead of silently missing data when the bounded buffer overflows.
    /// </remarks>
    public IAsyncEnumerable<TmuxEvent> Events { get; }

    /// <summary>Gets whether the client is still running.</summary>
    public bool IsRunning { get; }

    /// <summary>Runs one command on this client and reads what it answered.</summary>
    /// <param name="command">The typed command to run.</param>
    /// <param name="cancellationToken">Stops waiting for the answer.</param>
    /// <returns>The lines tmux printed, empty when it printed nothing.</returns>
    /// <remarks>
    /// tmux answers commands in the order it received them, so this is safe to
    /// call concurrently: each caller gets its own answer rather than someone
    /// else's. Cancelling stops the wait, not the command; tmux has already
    /// been told.
    /// </remarks>
    /// <exception cref="ControlModeCommandException">
    /// Tmux reported the command failed.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The rendered command is too large for one bounded request.
    /// </exception>
    /// <exception cref="StaleServerGenerationException">
    /// The command targets a different tmux server generation.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The client is no longer running or has too many unanswered commands.
    /// </exception>
    public Task<IReadOnlyList<string>> SendAsync(
        TmuxCommand command,
        CancellationToken cancellationToken = default);
}
