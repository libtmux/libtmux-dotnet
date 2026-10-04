using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace LibTmux.Internal;

internal static class PaneTextWaiter
{
    internal static readonly TimeSpan LongestTimeout = TimeSpan.FromDays(49);
    private const int MaximumMatchWorkBytes = PaneWaitRequest.MaximumMatchWorkBytes;

    [UnsupportedOSPlatform("windows")]
    internal static async Task<PaneWaitResult> WaitAsync(
        PaneActivityHub activity,
        Pane pane,
        ValidatedPaneWaitRequest options,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? matchLines,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? tailLines,
        Action<TimeSpan, TimeSpan, string>? progress,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? afterEntry = null,
        TimeProvider? finalTimerProvider = null,
        string? typedEcho = null,
        bool readThroughControl = true)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(pane);
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch elapsed = Stopwatch.StartNew();
        // Leave part of the timeout for the final grid read.
        TimeSpan finalReadReserve = TimeSpan.FromTicks(Math.Min(
            TimeSpan.FromMilliseconds(100).Ticks, options.Timeout.Ticks / 4));
        TimeSpan observationWindow = options.Timeout - finalReadReserve;
        using CancellationTokenSource deadline = new(options.Timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, activity.LifetimeToken, deadline.Token);
        CancellationToken token = linked.Token;
        PaneText.TypedEchoProjection? typedProjection = null;
        IAsyncDisposable? lease = null;
        bool pollingFallback = false;
        bool linesMissed = false;
        bool anchorLost = false;
        IReadOnlyList<string> lastTail = [];
        PaneWaitOutcome? confirmedOutcome = null;
        string? confirmedPattern = null;
        PaneWaitResult DeadlineResult()
        {
            IReadOnlyList<string> projected = typedProjection is null
                ? tailLines?.Invoke(lastTail) ?? lastTail
                : typedProjection.LastCompleted;
            if (typedProjection is not null
                && !ReferenceEquals(lastTail, typedProjection.LastInput))
            {
                linesMissed = true;
            }
            BoundedTail bounded = BoundTail(projected, options.TailLines, options.MaxOutputBytes);
            long dropped = lease is null ? 0 : PaneActivityHub.EventsDropped(lease);
            return new PaneWaitResult(
                pane.Id,
                confirmedOutcome ?? PaneWaitOutcome.TimedOut,
                confirmedPattern,
                bounded.Lines,
                elapsed.Elapsed,
                options.Timeout,
                dropped,
                pollingFallback,
                pollingFallback ? PaneActivityHub.PollInterval : null,
                linesMissed || pollingFallback || dropped > 0,
                anchorLost,
                bounded.OmittedLines,
                bounded.OmittedBytes);
        }

        try
        {
            if (typedEcho is { Length: > 0 })
            {
                typedProjection = new PaneText.TypedEchoProjection(
                    typedEcho, () => options.Timeout - elapsed.Elapsed, token);
                matchLines = typedProjection.Project;
                tailLines = typedProjection.Project;
            }

            try
            {
                lease = await activity.WatchAsync(pane, options.AllowPollingFallback, token)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Exception explained = await PaneReader.ExplainFailureAsync(pane, error, token)
                    .ConfigureAwait(false);
                if (ReferenceEquals(explained, error))
                {
                    throw;
                }

                throw explained;
            }
            await using ConfiguredAsyncDisposable watchedLease = lease.ConfigureAwait(false);
            IControlModeSession? control = readThroughControl ? activity.ControlFor(pane) : null;
            object? entrySignal = activity.CaptureSignal(pane);
            pollingFallback = PaneActivityHub.FallbackAtAcquisition(lease)
                || PaneActivityHub.RequireObservation(lease, entrySignal);
            Session capturedSession = pane.Session;
            long? observedArrangementVersion = activity.ArrangementVersion(pane);
            bool placementLost = false;
            bool paneGone = false;
            string? originalPid = null;
            int matchingWork = 0;
            PaneCursor? cursor = null;
            Dictionary<int, string>? triggerBaseline = null;
            int triggerFrontier = -1;
            bool triggerNeedsVisibleRebase = false;
            TimeSpan RemainingMatchBudget() => options.Timeout - elapsed.Elapsed;

            void RebaseTrigger(PaneRead read)
            {
                IReadOnlyList<string> projected = ProjectMatch(read.Lines);
                if (projected.Count != read.Lines.Count)
                {
                    triggerBaseline = null;
                    linesMissed = true;
                    return;
                }

                Dictionary<int, string> baseline = [];
                int bytes = 0;
                for (int index = 0; index < projected.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    int lineBytes = Encoding.UTF8.GetByteCount(projected[index]);
                    if (lineBytes > MaximumMatchWorkBytes - bytes)
                    {
                        throw new MatchWorkExceededException();
                    }

                    bytes += lineBytes;
                    baseline.Add(checked(read.State.HistorySize + index), projected[index]);
                }

                triggerBaseline = baseline;
                triggerFrontier = checked(read.State.HistorySize + read.Lines.Count - 1);
                triggerNeedsVisibleRebase = false;
            }

            IReadOnlyList<string> ProjectTrigger(PaneRead read, out MatchSpan[] spans)
            {
                spans = [];
                if (read.AnchorLost)
                {
                    RebaseTrigger(read);
                    return [];
                }

                if (triggerBaseline is null || read.RowPositions is null
                    || read.RowPositions.Count != read.Lines.Count)
                {
                    linesMissed = true;
                    triggerNeedsVisibleRebase = true;
                    return [];
                }

                IReadOnlyList<string> projected = read.Lines.Count == 0
                    && typedProjection is not null ? read.Lines : ProjectMatch(read.Lines);
                if (projected.Count != read.RowPositions.Count)
                {
                    triggerBaseline = null;
                    linesMissed = true;
                    triggerNeedsVisibleRebase = true;
                    return [];
                }

                if (read.AnchorShift != 0)
                {
                    // A recovered cursor identifies an anchor, but its old absolute
                    // coordinates no longer identify every row in our baseline.
                    linesMissed = true;
                    triggerNeedsVisibleRebase = true;
                    return [];
                }

                spans = new MatchSpan[projected.Count];
                int bytes = 0;
                for (int index = 0; index < projected.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    string current = projected[index];
                    int lineBytes = Encoding.UTF8.GetByteCount(current);
                    if (lineBytes > MaximumMatchWorkBytes - bytes)
                    {
                        throw new MatchWorkExceededException();
                    }

                    bytes += lineBytes;
                    int position = read.RowPositions[index];
                    if (triggerBaseline.TryGetValue(position, out string? old))
                    {
                        spans[index] = ChangedSpan(old, current, token);
                    }
                    else if (position > triggerFrontier)
                    {
                        spans[index] = new MatchSpan(0, current.Length, true);
                    }
                    else
                    {
                        // A cursor moving up can report old history that was never
                        // visible at entry. Its provenance is unknown, not new.
                        linesMissed = true;
                        triggerNeedsVisibleRebase = true;
                    }
                }

                return projected;
            }

            async Task RebaseFromVisibleAsync()
            {
                if (!triggerNeedsVisibleRebase)
                {
                    return;
                }

                try
                {
                    PaneRead visible = await ReadVisibleAsync(pane, originalPid, control, token)
                        .ConfigureAwait(false);
                    RebaseTrigger(visible);
                }
                catch (Exception error) when (error is PaneGoneException
                    or PaneReplacedException)
                {
                    paneGone = true;
                }
                catch (UnstableSnapshotException)
                {
                    triggerBaseline = null;
                    linesMissed = true;
                }
            }

            async Task CheckPlacementAsync()
            {
                try
                {
                    if ((await capturedSession.GetPanesAsync(token).ConfigureAwait(false))
                        .Any(candidate => candidate.Id == pane.Id))
                    {
                        return;
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    if (await PaneIsGoneAsync(pane, token).ConfigureAwait(false))
                    {
                        paneGone = true;
                        return;
                    }

                    throw;
                }

                if (await PaneIsGoneAsync(pane, token).ConfigureAwait(false))
                {
                    paneGone = true;
                    return;
                }

                if (!options.AllowPollingFallback)
                {
                    throw new TmuxTransportException(
                        $"Observed pane {pane.Id} is no longer linked to session "
                        + $"{capturedSession.Id}. Reacquire the pane in a current session.",
                        [], TmuxDispatchState.NotDispatched);
                }

                placementLost = true;
                pollingFallback = true;
                linesMissed = true;
            }

            await CheckPlacementAsync().ConfigureAwait(false);

            async Task<PaneWaitResult> FinishAsync(
                PaneWaitOutcome outcome,
                string? matchedPattern)
            {
                if (outcome != PaneWaitOutcome.TimedOut)
                {
                    confirmedOutcome = outcome;
                    confirmedPattern = matchedPattern;
                }

                if (paneGone && outcome == PaneWaitOutcome.TimedOut)
                {
                    outcome = PaneWaitOutcome.PaneExited;
                }

                while (!paneGone)
                {
                    object? finalSignal = null;
                    if (outcome == PaneWaitOutcome.TimedOut)
                    {
                        finalSignal = placementLost ? null : activity.CaptureSignal(pane);
                        await RecheckArrangementAsync(finalSignal is null).ConfigureAwait(false);
                        if (placementLost)
                        {
                            finalSignal = null;
                        }

                        if (paneGone)
                        {
                            outcome = PaneWaitOutcome.PaneExited;
                            break;
                        }

                        pollingFallback |= PaneActivityHub.RequireObservation(lease, finalSignal);
                    }

                    if (outcome == PaneWaitOutcome.TimedOut
                        && afterEntry is not null && cursor is not null)
                    {
                        try
                        {
                            PaneRead delta = await ReadSinceAsync(pane, cursor, control, token)
                                .ConfigureAwait(false);
                            linesMissed |= delta.LinesMissed;
                            anchorLost |= delta.AnchorLost;
                            cursor = PaneCursor.Build(pane, delta.State, delta.CursorRows);
                            if (delta.Lines.Count > 0)
                            {
                                lastTail = delta.Lines;
                            }

                            IReadOnlyList<string> changedLines = ProjectTrigger(delta,
                                out MatchSpan[] changedSpans);
                            if (Match(options.Stops, changedLines, ref matchingWork, token,
                                RemainingMatchBudget, changedSpans) is string stopped)
                            {
                                outcome = PaneWaitOutcome.Stopped;
                                matchedPattern = stopped;
                            }
                            else if (Match(options.Wanted, changedLines, ref matchingWork, token,
                                RemainingMatchBudget, changedSpans) is string hit)
                            {
                                outcome = PaneWaitOutcome.Matched;
                                matchedPattern = hit;
                            }
                            else if (options.Wanted.Length == 0
                                && changedSpans.Any(static span => span.Changed))
                            {
                                outcome = PaneWaitOutcome.AnyOutput;
                            }

                            if (outcome != PaneWaitOutcome.TimedOut)
                            {
                                confirmedOutcome = outcome;
                                confirmedPattern = matchedPattern;
                            }
                        }
                        catch (Exception error) when (error is PaneGoneException
                            or PaneReplacedException)
                        {
                            outcome = PaneWaitOutcome.PaneExited;
                        }
                        catch (UnstableSnapshotException)
                        {
                            linesMissed = true;
                        }
                    }

                    PaneRead? final = null;
                    try
                    {
                        final = await ReadVisibleAsync(
                                pane, originalPid, control, token)
                            .ConfigureAwait(false);
                        lastTail = final.Lines;
                    }
                    catch (Exception error) when (error is PaneGoneException
                        or PaneReplacedException)
                    {
                        if (outcome == PaneWaitOutcome.TimedOut)
                        {
                            outcome = PaneWaitOutcome.PaneExited;
                        }
                    }
                    catch (UnstableSnapshotException)
                    {
                        // The last stable read is a better result than an unstable final grid.
                    }

                    if (outcome == PaneWaitOutcome.TimedOut && final is not null)
                    {
                        await RecheckArrangementAsync().ConfigureAwait(false);
                        if (placementLost)
                        {
                            finalSignal = null;
                        }

                        if (afterEntry is not null)
                        {
                            if (triggerNeedsVisibleRebase)
                            {
                                RebaseTrigger(final);
                            }
                        }
                        else
                        {
                            IReadOnlyList<string> visible = ProjectMatch(final.Lines);
                            if (Match(options.Stops, visible, ref matchingWork, token,
                                RemainingMatchBudget) is string stopped)
                            {
                                outcome = PaneWaitOutcome.Stopped;
                                matchedPattern = stopped;
                            }
                            else if (Match(options.Wanted, visible, ref matchingWork, token,
                                RemainingMatchBudget) is string hit)
                            {
                                outcome = PaneWaitOutcome.Matched;
                                matchedPattern = hit;
                            }
                            else if (options.Wanted.Length == 0 && cursor is not null)
                            {
                                try
                                {
                                    PaneRead delta = await ReadSinceAsync(
                                            pane, cursor, control, token)
                                        .ConfigureAwait(false);
                                    linesMissed |= delta.LinesMissed;
                                    anchorLost |= delta.AnchorLost;
                                    if (!delta.LinesMissed && ProjectMatch(delta.Lines).Count > 0)
                                    {
                                        lastTail = delta.Lines;
                                        outcome = PaneWaitOutcome.AnyOutput;
                                    }
                                }
                                catch (Exception error) when (error is PaneGoneException
                                    or PaneReplacedException)
                                {
                                    outcome = PaneWaitOutcome.PaneExited;
                                }
                                catch (UnstableSnapshotException)
                                {
                                    linesMissed = true;
                                }
                            }
                        }
                        if (outcome == PaneWaitOutcome.TimedOut && final.State.Dead)
                        {
                            outcome = PaneWaitOutcome.PaneExited;
                        }
                    }

                    if (outcome != PaneWaitOutcome.TimedOut)
                    {
                        confirmedOutcome ??= outcome;
                        confirmedPattern ??= matchedPattern;
                        break;
                    }

                    TimeSpan remaining = options.Timeout - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    bool changed = await activity.WaitForActivityAsync(
                            pane.Id.ToString(), finalSignal, remaining, token,
                            lease, finalTimerProvider ?? TimeProvider.System)
                        .ConfigureAwait(false);
                    if (!changed && finalSignal is Task wake)
                    {
                        if (elapsed.Elapsed >= options.Timeout)
                        {
                            break;
                        }

                        // Task.WaitAsync rounds its timeout down to milliseconds.
                        // Keep the same subscription until output or the linked
                        // deadline fires, without starting a new read or timer.
                        await wake.WaitAsync(token).ConfigureAwait(false);
                    }
                }

                IReadOnlyList<string> projected = tailLines?.Invoke(lastTail) ?? lastTail;
                BoundedTail bounded = BoundTail(projected, options.TailLines, options.MaxOutputBytes);
                return new PaneWaitResult(
                    pane.Id,
                    outcome,
                    matchedPattern,
                    bounded.Lines,
                    elapsed.Elapsed,
                    options.Timeout,
                    PaneActivityHub.EventsDropped(lease),
                    pollingFallback,
                    pollingFallback ? PaneActivityHub.PollInterval : null,
                    linesMissed || pollingFallback || PaneActivityHub.EventsDropped(lease) > 0,
                    anchorLost,
                    bounded.OmittedLines,
                    bounded.OmittedBytes);
            }

            IReadOnlyList<string> ProjectMatch(IReadOnlyList<string> lines) =>
                matchLines?.Invoke(lines) ?? lines;

            if (paneGone)
            {
                throw new TmuxObjectNotFoundException(
                    $"tmux no longer has pane '{pane.Id}'.", pane.Id.ToString());
            }

            PaneRead? first = null;
            while (first is null)
            {
                object? retrySignal = activity.CaptureSignal(pane);
                pollingFallback |= PaneActivityHub.RequireObservation(lease, retrySignal);
                try
                {
                    first = await ReadVisibleAsync(pane, null, control, token)
                        .ConfigureAwait(false);
                }
                catch (PaneGoneException error)
                {
                    throw new TmuxObjectNotFoundException(
                        $"tmux no longer has pane '{pane.Id}'.", pane.Id.ToString(), error);
                }
                catch (UnstableSnapshotException)
                {
                    if (elapsed.Elapsed >= options.Timeout)
                    {
                        if (afterEntry is not null)
                        {
                            throw EntryReadRefusal(pane);
                        }

                        return DeadlineResult();
                    }

                    try
                    {
                        await activity.WaitForActivityAsync(
                            pane.Id.ToString(), retrySignal, options.Timeout - elapsed.Elapsed,
                            token, lease).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (afterEntry is not null
                        && deadline.IsCancellationRequested
                        && !cancellationToken.IsCancellationRequested
                        && !activity.LifetimeToken.IsCancellationRequested)
                    {
                        throw EntryReadRefusal(pane);
                    }
                }
            }

            originalPid = first.State.PanePid;
            lastTail = first.Lines;
            await RecheckArrangementAsync().ConfigureAwait(false);
            if (first.State.Dead)
            {
                return await FinishAsync(PaneWaitOutcome.PaneExited, null).ConfigureAwait(false);
            }

            if (afterEntry is null)
            {
                IReadOnlyList<string> visibleAtEntry = ProjectMatch(first.Lines);
                if (Match(options.Stops, visibleAtEntry, ref matchingWork, token,
                    RemainingMatchBudget) is string stoppedAtEntry)
                {
                    return await FinishAsync(PaneWaitOutcome.Stopped, stoppedAtEntry)
                        .ConfigureAwait(false);
                }

                if (Match(options.Wanted, visibleAtEntry, ref matchingWork, token,
                    RemainingMatchBudget) is string present)
                {
                    return await FinishAsync(PaneWaitOutcome.PresentAtEntry, present)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                RebaseTrigger(first);
            }

            cursor = PaneCursor.Build(pane, first.State, first.CursorRows);
            if (afterEntry is not null)
            {
                await afterEntry(token).ConfigureAwait(false);
            }

            while (true)
            {
                TimeSpan remaining = observationWindow - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return await FinishAsync(PaneWaitOutcome.TimedOut, null).ConfigureAwait(false);
                }

                // Capture the wake token before reading: a concurrent output event
                // must wake the wait even if the capture did not include its text.
                object? signal = placementLost ? null : activity.CaptureSignal(pane);
                await RecheckArrangementAsync(signal is null).ConfigureAwait(false);
                if (paneGone)
                {
                    return await FinishAsync(PaneWaitOutcome.PaneExited, null).ConfigureAwait(false);
                }
                if (placementLost)
                {
                    signal = null;
                }

                pollingFallback |= PaneActivityHub.RequireObservation(lease, signal);
                PaneRead read;
                try
                {
                    read = await ReadSinceAsync(pane, cursor, control, token)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (error is PaneGoneException
                    or PaneReplacedException)
                {
                    return await FinishAsync(PaneWaitOutcome.PaneExited, null).ConfigureAwait(false);
                }
                catch (UnstableSnapshotException)
                {
                    progress?.Invoke(elapsed.Elapsed, options.Timeout, $"waiting on {pane.Id}");
                    bool changed = await activity.WaitForActivityAsync(
                            pane.Id.ToString(), signal, observationWindow - elapsed.Elapsed,
                            token, lease)
                        .ConfigureAwait(false);
                    if (!changed && signal is Task)
                    {
                        return await FinishAsync(PaneWaitOutcome.TimedOut, null)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                cursor = PaneCursor.Build(pane, read.State, read.CursorRows);
                await RecheckArrangementAsync().ConfigureAwait(false);
                linesMissed |= read.LinesMissed;
                anchorLost |= read.AnchorLost;
                if (read.Lines.Count > 0)
                {
                    lastTail = read.Lines;
                }

                MatchSpan[]? matchSpans = null;
                IReadOnlyList<string> visible = afterEntry is not null
                    ? ProjectTrigger(read, out matchSpans)
                    : read.AnchorLost && options.Wanted.Length == 0
                        ? [] : ProjectMatch(read.Lines);
                if (Match(options.Stops, visible, ref matchingWork, token,
                    RemainingMatchBudget, matchSpans) is string stopped)
                {
                    return await FinishAsync(PaneWaitOutcome.Stopped, stopped)
                        .ConfigureAwait(false);
                }

                if (options.Wanted.Length == 0 && (matchSpans is null
                    ? visible.Count > 0
                    : matchSpans.Any(static span => span.Changed)))
                {
                    return await FinishAsync(PaneWaitOutcome.AnyOutput, null)
                        .ConfigureAwait(false);
                }

                if (Match(options.Wanted, visible, ref matchingWork, token,
                    RemainingMatchBudget, matchSpans) is string matched)
                {
                    return await FinishAsync(PaneWaitOutcome.Matched, matched)
                        .ConfigureAwait(false);
                }

                if (read.State.Dead)
                {
                    return await FinishAsync(PaneWaitOutcome.PaneExited, null)
                        .ConfigureAwait(false);
                }

                if (afterEntry is not null)
                {
                    await RebaseFromVisibleAsync().ConfigureAwait(false);
                    if (paneGone)
                    {
                        return await FinishAsync(PaneWaitOutcome.PaneExited, null)
                            .ConfigureAwait(false);
                    }
                }

                progress?.Invoke(
                    elapsed.Elapsed, options.Timeout,
                    read.Lines.Count > 0 ? read.Lines[^1] : $"waiting on {pane.Id}");

                bool activityObserved = await activity.WaitForActivityAsync(
                        pane.Id.ToString(), signal, observationWindow - elapsed.Elapsed,
                        token, lease)
                    .ConfigureAwait(false);
                if (!activityObserved && signal is Task)
                {
                    return await FinishAsync(PaneWaitOutcome.TimedOut, null)
                        .ConfigureAwait(false);
                }
            }

            async Task RecheckArrangementAsync(bool observationMissing = false)
            {
                long? currentArrangementVersion = activity.ArrangementVersion(pane);
                if (!placementLost && (observationMissing || pollingFallback
                    || currentArrangementVersion != observedArrangementVersion))
                {
                    observedArrangementVersion = currentArrangementVersion;
                    await CheckPlacementAsync().ConfigureAwait(false);
                }
                else if (placementLost)
                {
                    paneGone = await PaneIsGoneAsync(pane, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested
            && !activity.LifetimeToken.IsCancellationRequested)
        {
            return DeadlineResult();
        }
        catch (RegexMatchTimeoutException) when ((elapsed.Elapsed >= options.Timeout
            || confirmedOutcome is not null && typedProjection is not null)
            && !cancellationToken.IsCancellationRequested
            && !activity.LifetimeToken.IsCancellationRequested)
        {
            return DeadlineResult();
        }
        catch (PaneText.TypedEchoProjection.ProjectionDeadlineException) when (
            !cancellationToken.IsCancellationRequested
            && !activity.LifetimeToken.IsCancellationRequested)
        {
            return DeadlineResult();
        }
        catch (MatchWorkExceededException) when ((elapsed.Elapsed >= options.Timeout
            || confirmedOutcome is not null && typedProjection is not null)
            && !cancellationToken.IsCancellationRequested
            && !activity.LifetimeToken.IsCancellationRequested)
        {
            return DeadlineResult();
        }
        catch (PaneText.TypedEchoProjection.ProjectionWorkExceededException) when (
            (elapsed.Elapsed >= options.Timeout
                || confirmedOutcome is not null && typedProjection is not null)
            && !cancellationToken.IsCancellationRequested
            && !activity.LifetimeToken.IsCancellationRequested)
        {
            return DeadlineResult();
        }
    }

    /// <summary>Waits for a whole-screen predicate using the same streamed grid reader.</summary>
    [UnsupportedOSPlatform("windows")]
    internal static async Task<PaneWaitResult> WaitForScreenAsync(
        Pane pane,
        PaneActivityHub activity,
        Func<IReadOnlyList<string>, bool> condition,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(condition);
        ValidatedPaneWaitRequest options = new PaneWaitRequest { Timeout = budget }.Snapshot();
        Stopwatch elapsed = Stopwatch.StartNew();
        TimeSpan reserve = TimeSpan.FromTicks(Math.Min(
            TimeSpan.FromMilliseconds(100).Ticks, budget.Ticks / 4));
        TimeSpan observationWindow = budget - reserve;
        using CancellationTokenSource deadline = new(budget);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, activity.LifetimeToken, deadline.Token);
        CancellationToken token = linked.Token;
        IAsyncDisposable? lease = null;
        IReadOnlyList<string> lastLines = [];
        string? pid = null;
        bool linesMissed = false;
        try
        {
            try
            {
                lease = await activity.WatchAsync(pane, cancellationToken: token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Exception explained = await PaneReader.ExplainFailureAsync(pane, error, token)
                    .ConfigureAwait(false);
                if (ReferenceEquals(explained, error))
                {
                    throw;
                }

                throw explained;
            }
            await using ConfiguredAsyncDisposable watchedLease = lease.ConfigureAwait(false);
            IControlModeSession? control = activity.ControlFor(pane);
            Session originalSession = pane.Session;
            long? arrangement = activity.ArrangementVersion(pane);
            bool atEntry = true;

            PaneWaitResult Result(PaneWaitOutcome outcome)
            {
                BoundedTail tail = BoundTail(lastLines, options.TailLines, options.MaxOutputBytes);
                long dropped = PaneActivityHub.EventsDropped(lease);
                return new PaneWaitResult(
                    pane.Id, outcome, null, tail.Lines, elapsed.Elapsed, budget,
                    dropped, false, null, linesMissed || dropped > 0,
                    false, tail.OmittedLines, tail.OmittedBytes);
            }

            async Task<bool> StillPlacedAsync()
            {
                try
                {
                    if ((await originalSession.GetPanesAsync(token).ConfigureAwait(false))
                        .Any(candidate => candidate.Id == pane.Id))
                    {
                        return true;
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    if (await PaneIsGoneAsync(pane, token).ConfigureAwait(false))
                    {
                        return false;
                    }

                    throw;
                }

                if (await PaneIsGoneAsync(pane, token).ConfigureAwait(false))
                {
                    return false;
                }

                throw new TmuxTransportException(
                    $"Observed pane {pane.Id} is no longer linked to session {originalSession.Id}.",
                    [], TmuxDispatchState.NotDispatched);
            }

            if (!await StillPlacedAsync().ConfigureAwait(false))
            {
                throw new TmuxObjectNotFoundException(
                    $"tmux no longer has pane '{pane.Id}'.", pane.Id.ToString());
            }

            while (true)
            {
                object? signal = activity.CaptureSignal(pane);
                _ = PaneActivityHub.RequireObservation(lease, signal);
                long? currentArrangement = activity.ArrangementVersion(pane);
                if (currentArrangement != arrangement)
                {
                    arrangement = currentArrangement;
                    if (!await StillPlacedAsync().ConfigureAwait(false))
                    {
                        return Result(PaneWaitOutcome.PaneExited);
                    }
                }

                PaneRead read;
                try
                {
                    read = await ReadVisibleAsync(pane, pid, control, token).ConfigureAwait(false);
                }
                catch (PaneGoneException error) when (atEntry)
                {
                    throw new TmuxObjectNotFoundException(
                        $"tmux no longer has pane '{pane.Id}'.", pane.Id.ToString(), error);
                }
                catch (Exception error) when (error is PaneGoneException or PaneReplacedException)
                {
                    return Result(PaneWaitOutcome.PaneExited);
                }
                catch (UnstableSnapshotException)
                {
                    linesMissed = true;
                    if (elapsed.Elapsed >= budget)
                    {
                        return Result(PaneWaitOutcome.TimedOut);
                    }

                    await activity.WaitForActivityAsync(
                        pane.Id.ToString(), signal, budget - elapsed.Elapsed, token,
                        lease, TimeProvider.System).ConfigureAwait(false);
                    continue;
                }

                pid = read.State.PanePid;
                lastLines = read.Lines;
                if (condition(read.Lines))
                {
                    return Result(atEntry ? PaneWaitOutcome.PresentAtEntry : PaneWaitOutcome.Matched);
                }

                if (read.State.Dead)
                {
                    return Result(PaneWaitOutcome.PaneExited);
                }

                atEntry = false;
                TimeSpan remaining = (elapsed.Elapsed < observationWindow
                    ? observationWindow : budget) - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return Result(PaneWaitOutcome.TimedOut);
                }

                bool changed = await activity.WaitForActivityAsync(
                    pane.Id.ToString(), signal, remaining, token,
                    lease, elapsed.Elapsed >= observationWindow ? TimeProvider.System : null)
                    .ConfigureAwait(false);
                if (!changed && elapsed.Elapsed >= observationWindow)
                {
                    return Result(PaneWaitOutcome.TimedOut);
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested
            && !activity.LifetimeToken.IsCancellationRequested)
        {
            BoundedTail tail = BoundTail(lastLines, options.TailLines, options.MaxOutputBytes);
            long dropped = lease is null ? 0 : PaneActivityHub.EventsDropped(lease);
            return new PaneWaitResult(
                pane.Id, PaneWaitOutcome.TimedOut, null, tail.Lines, elapsed.Elapsed,
                budget, dropped, false, null, linesMissed || dropped > 0,
                false, tail.OmittedLines, tail.OmittedBytes);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static Task<PaneRead> ReadVisibleAsync(
        Pane pane,
        string? baselinePid,
        IControlModeSession? control,
        CancellationToken cancellationToken) =>
        ReadAsync(() => PaneReader.ReadVisibleAsync(
            pane, baselinePid, Failure, control, cancellationToken, rejectDeadAtEntry: false),
            pane, cancellationToken);

    [UnsupportedOSPlatform("windows")]
    private static Task<PaneRead> ReadSinceAsync(
        Pane pane,
        PaneCursor cursor,
        IControlModeSession? control,
        CancellationToken cancellationToken) =>
        ReadAsync(() => PaneReader.ReadSinceAsync(pane, cursor, Failure, control, cancellationToken),
            pane, cancellationToken);

    [UnsupportedOSPlatform("windows")]
    private static async Task<PaneRead> ReadAsync(
        Func<Task<PaneRead>> read,
        Pane pane,
        CancellationToken cancellationToken)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException
            and not UnstableSnapshotException and not PaneReplacedException)
        {
            if (await PaneIsGoneAsync(pane, cancellationToken).ConfigureAwait(false))
            {
                throw new PaneGoneException(pane.Id);
            }

            throw;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static TmuxPaneException EntryReadRefusal(Pane pane) => new(
        $"Pane {pane.Id} changed during every snapshot attempt until the timeout. No keys were sent.",
        pane.Id,
        TmuxDispatchState.NotDispatched);

    [UnsupportedOSPlatform("windows")]
    private static Exception Failure(PaneReadFailure failure, Pane pane) => failure switch
    {
        PaneReadFailure.Replaced => new PaneReplacedException(pane.Id),
        PaneReadFailure.Unstable => new UnstableSnapshotException(pane.Id),
        PaneReadFailure.Unreported => new TmuxPaneException(
            $"tmux did not report the state of pane {pane.Id}.", pane.Id),
        _ => PaneReader.Failure(failure, pane),
    };

    [UnsupportedOSPlatform("windows")]
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

    internal static string? Match(
        PaneWaitPattern[] patterns,
        IReadOnlyList<string> lines,
        ref int matchingWork,
        CancellationToken cancellationToken,
        Func<TimeSpan>? remainingBudget = null,
        IReadOnlyList<MatchSpan>? changedSpans = null)
    {
        if (changedSpans is not null && changedSpans.Count != lines.Count)
        {
            throw new ArgumentException("Match spans must align with their lines.", nameof(changedSpans));
        }

        foreach (PaneWaitPattern pattern in patterns)
        {
            for (int index = 0; index < lines.Count; index++)
            {
                string line = lines[index];
                cancellationToken.ThrowIfCancellationRequested();
                MatchSpan? changed = changedSpans is null ? null : changedSpans[index];
                if (changed is MatchSpan span && !span.Changed)
                {
                    continue;
                }

                TimeSpan? remaining = remainingBudget?.Invoke();
                if (remaining is TimeSpan exhausted && exhausted <= TimeSpan.Zero)
                {
                    throw new RegexMatchTimeoutException(
                        line, pattern.Regex.ToString(), pattern.Regex.MatchTimeout);
                }

                int bytes = Encoding.UTF8.GetByteCount(line);
                if (bytes > MaximumMatchWorkBytes - matchingWork)
                {
                    throw new MatchWorkExceededException();
                }

                matchingWork += bytes;
                Regex regex = pattern.Regex;
                if (remaining is TimeSpan available)
                {
                    if (available < regex.MatchTimeout)
                    {
                        regex = new Regex(regex.ToString(), regex.Options, available);
                    }
                }

                if (changed is null)
                {
                    if (regex.IsMatch(line))
                    {
                        return pattern.Text;
                    }

                    continue;
                }

                bool rightToLeft = regex.RightToLeft;
                int searchAt = rightToLeft ? line.Length : 0;
                while (rightToLeft ? searchAt >= 0 : searchAt <= line.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    remaining = remainingBudget?.Invoke();
                    if (remaining is TimeSpan left && left <= TimeSpan.Zero)
                    {
                        throw new RegexMatchTimeoutException(
                            line, pattern.Regex.ToString(), pattern.Regex.MatchTimeout);
                    }

                    Regex candidateRegex = regex;
                    if (remaining is TimeSpan availableNow
                        && availableNow < regex.MatchTimeout)
                    {
                        candidateRegex = new Regex(regex.ToString(), regex.Options, availableNow);
                    }

                    Match candidate = candidateRegex.Match(line, searchAt);
                    if (!candidate.Success)
                    {
                        break;
                    }

                    if (candidate.Length == 0
                        ? candidate.Index >= changed.Value.Start
                            && candidate.Index <= changed.Value.End
                        : candidate.Index < changed.Value.End
                            && candidate.Index + candidate.Length > changed.Value.Start)
                    {
                        return pattern.Text;
                    }

                    if (matchingWork == MaximumMatchWorkBytes)
                    {
                        throw new MatchWorkExceededException();
                    }

                    matchingWork++;
                    searchAt = rightToLeft
                        ? candidate.Index - 1
                        : candidate.Index + 1;
                }
            }
        }

        return null;
    }

    private static MatchSpan ChangedSpan(
        string old,
        string current,
        CancellationToken cancellationToken)
    {
        int prefix = 0;
        while (prefix < old.Length && prefix < current.Length
            && old[prefix] == current[prefix])
        {
            if ((prefix & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            prefix++;
        }

        int suffix = 0;
        while (suffix < old.Length - prefix && suffix < current.Length - prefix
            && old[old.Length - suffix - 1] == current[current.Length - suffix - 1])
        {
            if ((suffix & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            suffix++;
        }

        return new MatchSpan(prefix, current.Length - suffix,
            !string.Equals(old, current, StringComparison.Ordinal));
    }

    internal readonly record struct MatchSpan(int Start, int End, bool Changed);

    private static BoundedTail BoundTail(IReadOnlyList<string> lines, int maxLines, int maxBytes)
    {
        int start = Math.Max(0, lines.Count - maxLines);
        int omittedLines = start;
        int omittedBytes = 0;
        for (int index = 0; index < start; index++)
        {
            omittedBytes = checked(omittedBytes + Encoding.UTF8.GetByteCount(lines[index]));
        }

        List<string> selected = [];
        int remaining = maxBytes;
        for (int index = lines.Count - 1; index >= start; index--)
        {
            string line = lines[index];
            int bytes = Encoding.UTF8.GetByteCount(line);
            int separator = selected.Count == 0 ? 0 : 1;
            if (bytes + separator <= remaining)
            {
                selected.Add(line);
                remaining -= bytes + separator;
                continue;
            }

            if (selected.Count == 0)
            {
                string suffix = Utf8Suffix(line, remaining);
                selected.Add(suffix);
                omittedBytes = checked(omittedBytes + bytes - Encoding.UTF8.GetByteCount(suffix));
            }
            else
            {
                omittedLines++;
                omittedBytes = checked(omittedBytes + bytes);
            }

            for (int older = index - 1; older >= start; older--)
            {
                omittedLines++;
                omittedBytes = checked(omittedBytes + Encoding.UTF8.GetByteCount(lines[older]));
            }

            break;
        }

        selected.Reverse();
        return new BoundedTail(selected, omittedLines, omittedBytes);
    }

    private static string Utf8Suffix(string line, int maxBytes)
    {
        int start = line.Length;
        int used = 0;
        while (start > 0)
        {
            OperationStatus status = Rune.DecodeLastFromUtf16(
                line.AsSpan(0, start), out Rune rune, out int chars);
            if (status != OperationStatus.Done || used + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }

            start -= chars;
            used += rune.Utf8SequenceLength;
        }

        return line[start..];
    }

    private sealed record BoundedTail(IReadOnlyList<string> Lines, int OmittedLines, int OmittedBytes);

    private sealed class PaneGoneException(PaneId paneId)
        : IOException($"Pane {paneId} closed during a rendered text read.");

    private sealed class PaneReplacedException(PaneId paneId)
        : IOException($"Pane {paneId} started a different process during a rendered text read.");

    private sealed class UnstableSnapshotException(PaneId paneId)
        : IOException($"Pane {paneId} changed during every snapshot attempt.");

    internal sealed class MatchWorkExceededException()
        : IOException("Pane wait matching work exceeded 8388608 UTF-8 bytes.");
}
