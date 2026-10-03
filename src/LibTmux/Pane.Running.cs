using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Runs a shell command in a pane and learns its exit status.
public sealed partial class Pane
{
    private static readonly HashSet<string> PosixShells =
        new(["sh", "ash", "bash", "dash", "ksh", "ksh93", "mksh", "pdksh", "zsh"], StringComparer.Ordinal);

    /// <summary>Runs a shell command in the pane and waits for its exit status.</summary>
    /// <param name="command">The shell command.</param>
    /// <param name="timeout">How long to wait for it.</param>
    /// <param name="cancellationToken">Stops waiting; a command already sent keeps running.</param>
    /// <returns>The exit status and what the command printed.</returns>
    /// <inheritdoc cref="RunAsync(PaneRunRequest, CancellationToken)" path="/remarks" />
    /// <exception cref="ArgumentException"><paramref name="command" /> is blank.</exception>
    /// <exception cref="TmuxPaneException">The pane is not at a POSIX shell, is in a mode, or its program has exited.</exception>
    [UnsupportedOSPlatform("windows")]
    public Task<PaneRunResult> RunAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        RunAsync(new PaneRunRequest(command) { Timeout = timeout }, cancellationToken);

    /// <summary>Runs a shell command in the pane and waits for its exit status.</summary>
    /// <param name="request">The command and how long to wait.</param>
    /// <param name="cancellationToken">Stops waiting; a command already sent keeps running.</param>
    /// <returns>The exit status and what the command printed.</returns>
    /// <remarks>
    /// <para>
    /// The pane's shell sources a private file that runs the command in a
    /// subshell, so <c>cd</c> and <c>export</c> do not persist. Completion is
    /// not guessed from the screen: the file stores <c>$?</c> in a private pane
    /// option and signals a private <c>wait-for</c> channel.
    /// </para>
    /// <para>
    /// A command that outlasts the timeout keeps running and is followed until it
    /// finishes, so its option and file are removed. Do not start another
    /// command in the same pane before it ends: the shell would read both.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative.</exception>
    /// <exception cref="TmuxPaneException">The pane is not at a POSIX shell, is in a mode, or its program has exited.</exception>
    /// <exception cref="LibTmuxException">The command was sent but its result could not be read; inspect the pane before retrying.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<PaneRunResult> RunAsync(
        PaneRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Timeout, TimeSpan.Zero);

        // Pasted text goes to whatever owns the pane, so nothing is sent
        // unless that is an idle shell this payload is written for.
        Pane current = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        string command = current.CurrentCommand ?? string.Empty;
        string shell = command.TrimStart('-');
        if (current.RawFormatFields.GetValueOrDefault("pane_in_mode") is not "0"
            || !PosixShells.Contains(shell[(shell.LastIndexOf('/') + 1)..]))
        {
            throw new TmuxPaneException(
                $"Pane {Id} cannot run a command: it is running '{command}' or is in a mode, not waiting at a POSIX shell.",
                Id);
        }

        PaneRunOutcome outcome = await PaneRunner
            .RunAsync(
                current.Server,
                current,
                PaneRunRoute.From(current),
                request.Command,
                request.Timeout,
                request.KeepOutOfHistory,
                request.Timeout + TimeSpan.FromMinutes(1),
                new PaneRunHooks(),
                PaneReader.Failure,
                cancellationToken)
            .ConfigureAwait(false);
        return new PaneRunResult(
            outcome.ExitStatus,
            outcome.TimedOut,
            outcome.Output,
            outcome.Elapsed,
            outcome.Started,
            outcome.LinesMissed);
    }
}
