using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LibTmux.Internal;

/// <summary>How a classifier judged one batch of pane lines.</summary>
/// <param name="Outcome">The outcome the batch ends the wait with.</param>
/// <param name="Match">What matched, when anything did.</param>
internal readonly record struct PaneWaitVerdict(PaneWaitOutcome Outcome, string? Match);

/// <summary>Waits for a pane to print what a classifier is looking for.</summary>
/// <remarks>
/// The screen at entry is judged once, so text already showing is answered at
/// once rather than at the deadline. After that only rows the pane writes or
/// rewrites are judged, and the wait sleeps on the pane's control-mode output
/// between reads instead of polling.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class PaneTextWaiter
{
    /// <summary>The longest wait a timer can represent, with room for a run's cleanup delay.</summary>
    internal static readonly TimeSpan LongestTimeout = TimeSpan.FromDays(49);

    /// <summary>Runs one wait.</summary>
    /// <param name="pane">The pane to watch.</param>
    /// <param name="activity">Wakes the wait when the pane prints.</param>
    /// <param name="classify">Judges lines, told whether they are the screen at entry.</param>
    /// <param name="budget">How long the wait may run.</param>
    /// <param name="fail">Builds the exception for a read that cannot be completed.</param>
    /// <param name="progress">Told how long the wait has run and what the pane last showed.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <param name="afterEntry">Runs once the screen at entry is read, before anything later is; it may send keys.</param>
    /// <param name="readThroughControl">
    /// Whether to read through the control client the wait attaches, a round
    /// trip in place of a tmux process for each read.
    /// </param>
    /// <param name="observe">Receives the owned watch and signal before each read.</param>
    /// <returns>How the wait ended, what matched, and how long it took.</returns>
    internal static async Task<(PaneWaitOutcome Outcome, string? Match, TimeSpan Elapsed)> WaitAsync(
        Pane pane,
        PaneActivityHub activity,
        Func<IReadOnlyList<string>, bool, PaneWaitVerdict?> classify,
        TimeSpan budget,
        Func<PaneReadFailure, Pane, Exception> fail,
        Action<TimeSpan, string>? progress,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? afterEntry = null,
        bool readThroughControl = true,
        Action<IAsyncDisposable, object?>? observe = null)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        IAsyncDisposable lease = await activity.WatchAsync(pane, cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable _ = lease.ConfigureAwait(false);
        IControlModeSession? control = readThroughControl ? activity.ControlFor(pane) : null;

        PaneRead? first = null;
        while (first is null)
        {
            object? entrySignal = activity.CaptureSignal(pane);
            observe?.Invoke(lease, entrySignal);
            try
            {
                first = await PaneReader.ReadVisibleAsync(pane, null, fail, control, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (PaneReader.IsUnstable(error))
            {
                // A pane busy through every attempt is read again at its next
                // output: the signal taken before the read has already fired.
                if (budget - elapsed.Elapsed <= TimeSpan.Zero)
                {
                    // Keys follow the read they are judged against, so none
                    // were sent, which a timeout would not say.
                    if (afterEntry is not null)
                    {
                        throw;
                    }

                    return (PaneWaitOutcome.TimedOut, null, elapsed.Elapsed);
                }

                bool signaled = await activity.WaitForActivityAsync(
                        pane.Id.ToString(),
                        entrySignal,
                        budget - elapsed.Elapsed,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!signaled && entrySignal is Task)
                {
                    if (afterEntry is not null)
                    {
                        throw;
                    }

                    return (PaneWaitOutcome.TimedOut, null, elapsed.Elapsed);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException and not TmuxPaneException)
            {
                Exception explained = await PaneReader.ExplainFailureAsync(pane, error, cancellationToken)
                    .ConfigureAwait(false);
                if (ReferenceEquals(explained, error))
                {
                    throw;
                }

                throw explained;
            }
        }

        PaneCursor cursor = PaneCursor.Build(pane, first.State, first.CursorRows);
        bool alternate = first.State.AlternateScreen;
        if (classify(first.Lines, true) is { } entry)
        {
            return (entry.Outcome, entry.Match, elapsed.Elapsed);
        }

        if (afterEntry is not null)
        {
            await afterEntry(cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            // Taken before the read, so output arriving during the read wakes
            // the next sleep instead of being slept through.
            object? signal = activity.CaptureSignal(pane);
            observe?.Invoke(lease, signal);
            PaneRead? read = null;
            try
            {
                read = await PaneReader.ReadSinceAsync(pane, cursor, fail, control, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (PaneReader.IsUnstable(error))
            {
                // Judged at the pane's next output instead; see the entry read.
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Without remain-on-exit a pane closes with its program, and
                // a closed pane has no state to read. Any other failure, or one
                // the lookup cannot settle, is the read's own.
                if (await PaneIsGoneAsync(pane, cancellationToken).ConfigureAwait(false))
                {
                    return (PaneWaitOutcome.PaneExited, null, elapsed.Elapsed);
                }

                ExceptionDispatchInfo.Capture(error).Throw();
                throw;
            }

            if (read is not null)
            {
                cursor = PaneCursor.Build(pane, read.State, read.CursorRows);
                if (read.Lines.Count > 0 && classify(read.Lines, false) is { } verdict)
                {
                    return (verdict.Outcome, verdict.Match, elapsed.Elapsed);
                }

                if (read.State.Dead)
                {
                    return (PaneWaitOutcome.PaneExited, null, elapsed.Elapsed);
                }

                // A full-screen program repaints rather than appending, so
                // "what is new" stops meaning anything.
                if (!alternate && read.State.AlternateScreen)
                {
                    return (PaneWaitOutcome.AlternateScreen, null, elapsed.Elapsed);
                }
            }

            // Checked after the read, so output that woke the last sleep as
            // the budget ran out is still judged.
            if (budget - elapsed.Elapsed <= TimeSpan.Zero)
            {
                return (PaneWaitOutcome.TimedOut, null, elapsed.Elapsed);
            }

            progress?.Invoke(elapsed.Elapsed, read is { Lines.Count: > 0 } ? read.Lines[^1] : string.Empty);
            bool signaled = await activity.WaitForActivityAsync(
                    pane.Id.ToString(),
                    signal,
                    budget - elapsed.Elapsed,
                    cancellationToken)
                .ConfigureAwait(false);
            // A control timer can finish before the elapsed clock reaches its deadline.
            if (!signaled && signal is Task)
            {
                return (PaneWaitOutcome.TimedOut, null, elapsed.Elapsed);
            }
        }
    }

    /// <summary>Waits until a condition holds over everything the pane shows.</summary>
    /// <param name="pane">The pane to watch.</param>
    /// <param name="activity">Wakes the wait when the pane prints or changes state.</param>
    /// <param name="condition">Judges the visible rows.</param>
    /// <param name="budget">How long the wait may run.</param>
    /// <param name="fail">Builds the exception for a read that cannot be completed.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>How the wait ended and how long it took.</returns>
    internal static async Task<(PaneWaitOutcome Outcome, TimeSpan Elapsed)> WaitForScreenAsync(
        Pane pane,
        PaneActivityHub activity,
        Func<IReadOnlyList<string>, bool> condition,
        TimeSpan budget,
        Func<PaneReadFailure, Pane, Exception> fail,
        CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        IAsyncDisposable lease = await activity.WatchAsync(pane, cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable _ = lease.ConfigureAwait(false);
        IControlModeSession? control = activity.ControlFor(pane);
        string? pid = null;
        while (true)
        {
            object? signal = activity.CaptureSignal(pane);
            PaneRead read;
            try
            {
                read = await PaneReader.ReadVisibleAsync(pane, pid, fail, control, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (PaneReader.IsUnstable(error))
            {
                // A pane busy through every attempt is judged at its next
                // output: the signal taken before the read has already fired.
                if (budget - elapsed.Elapsed <= TimeSpan.Zero)
                {
                    return (PaneWaitOutcome.TimedOut, elapsed.Elapsed);
                }

                await activity.WaitForActivityAsync(
                        pane.Id.ToString(),
                        signal,
                        budget - elapsed.Elapsed,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            catch (Exception error) when (pid is null
                && error is not OperationCanceledException and not TmuxPaneException)
            {
                Exception explained = await PaneReader.ExplainFailureAsync(pane, error, cancellationToken)
                    .ConfigureAwait(false);
                if (ReferenceEquals(explained, error))
                {
                    throw;
                }

                throw explained;
            }
            catch (Exception error) when (pid is not null && error is not OperationCanceledException)
            {
                if (await PaneIsGoneAsync(pane, cancellationToken).ConfigureAwait(false))
                {
                    return (PaneWaitOutcome.PaneExited, elapsed.Elapsed);
                }

                ExceptionDispatchInfo.Capture(error).Throw();
                throw;
            }

            if (pid is not null && !string.Equals(read.State.PanePid, pid, StringComparison.Ordinal))
            {
                throw fail(PaneReadFailure.Replaced, pane);
            }

            if (condition(read.Lines))
            {
                return (pid is null ? PaneWaitOutcome.PresentAtEntry : PaneWaitOutcome.Matched, elapsed.Elapsed);
            }

            if (read.State.Dead)
            {
                return (PaneWaitOutcome.PaneExited, elapsed.Elapsed);
            }

            if (budget - elapsed.Elapsed <= TimeSpan.Zero)
            {
                return (PaneWaitOutcome.TimedOut, elapsed.Elapsed);
            }

            pid = read.State.PanePid;
            await activity.WaitForActivityAsync(
                    pane.Id.ToString(),
                    signal,
                    budget - elapsed.Elapsed,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<bool> PaneIsGoneAsync(Pane pane, CancellationToken cancellationToken)
    {
        try
        {
            return await pane.Server.FindPaneAsync(pane.Id, cancellationToken).ConfigureAwait(false) is null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return false;
        }
    }
}
