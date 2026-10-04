using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux;

// Runs a shell command in a pane and learns its exit status.
public sealed partial class Pane
{
    private static readonly TimeSpan FollowAfterTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Runs a shell command in the pane and waits for its exit status.</summary>
    /// <param name="command">The shell command.</param>
    /// <param name="timeout">How long to wait for it.</param>
    /// <param name="cancellationToken">
    /// Stops waiting. A command already sent keeps running, so cancelling then raises
    /// <see cref="LibTmuxException" /> saying it may have run, not <see cref="OperationCanceledException" />,
    /// which <see cref="Task.Wait()" /> and F#'s <c>Async.AwaitTask</c> replace with a bare
    /// <see cref="TaskCanceledException" />.
    /// </param>
    /// <returns>The exit status and what the command printed.</returns>
    /// <inheritdoc cref="RunAsync(PaneRunRequest, CancellationToken)" path="/remarks" />
    /// <exception cref="ArgumentException"><paramref name="command" /> is blank.</exception>
    /// <exception cref="TmuxPaneException">The pane is not at a POSIX shell, is in a mode, or its program has exited; or it changed during every read before the command was sent.</exception>
    /// <exception cref="TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    /// <exception cref="LibTmuxException">The command's result could not be read, or its private files could not be deleted; inspect the failure before retrying.</exception>
    [UnsupportedOSPlatform("windows")]
    public Task<PaneRunResult> RunAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        RunAsync(new PaneRunRequest(command) { Timeout = timeout }, cancellationToken);

    /// <summary>Runs a shell command in the pane and waits for its exit status.</summary>
    /// <param name="request">The command and how long to wait.</param>
    /// <param name="cancellationToken">
    /// Stops waiting. A command already sent keeps running, so cancelling then raises
    /// <see cref="LibTmuxException" /> saying it may have run, not <see cref="OperationCanceledException" />,
    /// which <see cref="Task.Wait()" /> and F#'s <c>Async.AwaitTask</c> replace with a bare
    /// <see cref="TaskCanceledException" />.
    /// </param>
    /// <returns>The exit status and what the command printed.</returns>
    /// <remarks>
    /// <para>
    /// The pane's shell sources a private file that runs the command in a
    /// subshell, so <c>cd</c> and <c>export</c> do not persist. Completion is
    /// not guessed from the screen: the file stores <c>$?</c> in a private pane
    /// option and signals a private <c>wait-for</c> channel.
    /// </para>
    /// <para>
    /// A command that outlasts the timeout may keep running. The library follows
    /// it for one more minute, then keeps its same-pane reservation without
    /// polling. The next run may proceed only after the status option or the
    /// pane/server's end authenticates completion. Other processes are not
    /// coordinated.
    /// </para>
    /// <para>
    /// If a completed command's private files cannot be deleted, the failure's
    /// <c>Data["LibTmux.CompletedRunResult"]</c> contains its bounded
    /// <see cref="PaneRunResult" /> and <c>Data["LibTmux.RunDirectoryCleanupDirectory"]</c>
    /// names the owned directory to inspect and remove. Do not run the command again.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The timeout or output budget is invalid.</exception>
    /// <exception cref="TmuxPaneException">The pane is not at a POSIX shell, is in a mode, or its program has exited; or it changed during every read before the command was sent.</exception>
    /// <exception cref="TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    /// <exception cref="LibTmuxException">The command's result could not be read, or its private files could not be deleted; inspect the failure before retrying.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<PaneRunResult> RunAsync(
        PaneRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            PaneRunOutcome outcome = await PaneRunner
                .RunAsync(
                    Server,
                    this,
                    PaneRunRoute.From(this),
                    request.Command,
                    request.Timeout,
                    request.KeepOutOfHistory,
                    request.Timeout + FollowAfterTimeout,
                    new PaneRunHooks { FollowLimit = FollowAfterTimeout },
                    PaneReader.Failure,
                    cancellationToken)
                .ConfigureAwait(false);
            return ToRunResult(outcome, request);
        }
        catch (LibTmuxException error) when (error.Data[PaneRunner.CompletedRunOutcomeDataKey] is PaneRunOutcome)
        {
            var outcome = (PaneRunOutcome)error.Data[PaneRunner.CompletedRunOutcomeDataKey]!;
            error.Data.Remove(PaneRunner.CompletedRunOutcomeDataKey);
            error.Data[PaneRunner.CompletedRunResultDataKey] = ToRunResult(outcome, request);
            throw;
        }
    }

    private static PaneRunResult ToRunResult(PaneRunOutcome outcome, PaneRunRequest request)
    {
        (IReadOnlyList<string> output, int omittedLines, int omittedBytes) = BoundOutput(
            outcome.Output, request.MaxOutputLines, request.MaxOutputBytes);
        return new PaneRunResult(
            outcome.ExitStatus,
            outcome.TimedOut,
            output,
            outcome.Elapsed,
            outcome.Started,
            outcome.LinesMissed)
        {
            PaneId = outcome.Pane.Id,
            EffectiveTimeout = request.Timeout,
            AnchorLost = outcome.AnchorLost,
            OmittedOutputLines = omittedLines,
            OmittedOutputBytes = omittedBytes,
            PaneExited = outcome.PaneExited,
        };
    }

    private static (IReadOnlyList<string> Lines, int OmittedLines, int OmittedBytes) BoundOutput(
        IReadOnlyList<string> lines,
        int maxLines,
        int maxBytes)
    {
        int first = Math.Max(0, lines.Count - maxLines);
        var newestFirst = new List<string>(lines.Count - first);
        int retainedBytes = 0;
        int omittedLines = first;
        for (int index = lines.Count - 1; index >= first; index--)
        {
            int separator = newestFirst.Count > 0 ? 1 : 0;
            int remaining = maxBytes - retainedBytes - separator;
            if (remaining <= 0)
            {
                omittedLines += index - first + 1;
                break;
            }

            string line = lines[index];
            int lineBytes = Encoding.UTF8.GetByteCount(line);
            if (lineBytes > remaining)
            {
                string suffix = Utf8Suffix(line, remaining);
                if (suffix.Length > 0)
                {
                    newestFirst.Add(suffix);
                    retainedBytes += separator + Encoding.UTF8.GetByteCount(suffix);
                }
                else
                {
                    omittedLines++;
                }

                omittedLines += index - first;
                break;
            }

            newestFirst.Add(line);
            retainedBytes += separator + lineBytes;
        }

        newestFirst.Reverse();
        long totalBytes = lines.Count == 0 ? 0 : lines.Count - 1;
        foreach (string line in lines)
        {
            totalBytes += Encoding.UTF8.GetByteCount(line);
        }

        return (Array.AsReadOnly(newestFirst.ToArray()), omittedLines,
            (int)Math.Min(int.MaxValue, totalBytes - retainedBytes));
    }

    private static string Utf8Suffix(string line, int budget)
    {
        int start = line.Length;
        int used = 0;
        while (start > 0)
        {
            int chars = start > 1 && char.IsLowSurrogate(line[start - 1])
                && char.IsHighSurrogate(line[start - 2]) ? 2 : 1;
            int bytes = Encoding.UTF8.GetByteCount(line.AsSpan(start - chars, chars));
            if (used + bytes > budget)
            {
                break;
            }

            used += bytes;
            start -= chars;
        }

        return line[start..];
    }
}
