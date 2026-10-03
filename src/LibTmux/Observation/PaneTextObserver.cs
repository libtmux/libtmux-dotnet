using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux;

/// <summary>Waits for rendered pane text using an owned, shared control client.</summary>
/// <remarks>
/// A control client wakes the wait; text always comes from a pane capture.
/// The observer borrows the pane and tmux daemon. Dispose it when waits end.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class PaneTextObserver : IAsyncDisposable
{
    private readonly PaneActivitySource _activity;

    /// <summary>Creates an observer that requires control-mode notification.</summary>
    /// <param name="logger">Reports control-client and fallback failures.</param>
    /// <param name="allowPollingFallback">Permits timed reads if control observation fails.</param>
    public PaneTextObserver(ILogger? logger = null, bool allowPollingFallback = false) =>
        _activity = new PaneActivitySource(logger, allowPollingFallback);

    internal PaneTextObserver(
        Func<Pane, CancellationToken, Task<IControlModeSession>> startPaneSession,
        ILogger? logger = null,
        bool allowPollingFallback = false,
        TimeProvider? timeProvider = null) =>
        _activity = new PaneActivitySource(
            startPaneSession, logger, allowPollingFallback, timeProvider);

    /// <summary>Waits until a pattern appears, a stop pattern appears, or the wait ends.</summary>
    /// <param name="pane">The pane to observe in its captured session.</param>
    /// <param name="request">Patterns and output limits, or defaults.</param>
    /// <param name="cancellationToken">Stops waiting and releases its control-client lease.</param>
    /// <returns>A typed outcome with a bounded rendered tail.</returns>
    public Task<PaneTextWaitResult> WaitForTextAsync(
        Pane pane,
        PaneTextWaitRequest? request = null,
        CancellationToken cancellationToken = default) =>
        PaneTextWaitEngine.WaitAsync(
            _activity, pane, request, matchLines: null, tailLines: null,
            progress: null, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _activity.DisposeAsync();
}

internal static class PaneTextWaitEngine
{
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromDays(1);
    private const int MaximumPatternBytes = 999;
    private const int MaximumPatternBytesTotal = 16_384;
    private const int MaximumMatchWorkBytes = 8 * 1024 * 1024;

    [UnsupportedOSPlatform("windows")]
    internal static async Task<PaneTextWaitResult> WaitAsync(
        PaneActivitySource activity,
        Pane pane,
        PaneTextWaitRequest? request,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? matchLines,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? tailLines,
        Action<TimeSpan, TimeSpan, string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(pane);
        ValidatedRequest options = Validate(request ?? new PaneTextWaitRequest());
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
        IAsyncDisposable? lease = null;
        bool pollingFallback = false;
        bool linesMissed = false;
        bool anchorLost = false;
        IReadOnlyList<string> lastTail = [];
        PaneTextWaitOutcome? confirmedOutcome = null;
        string? confirmedPattern = null;
        try
        {
            lease = await activity.WatchAsync(pane, token)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable _ = lease.ConfigureAwait(false);
            object? entrySignal = activity.CaptureSignal(pane);
            pollingFallback = activity.RequireObservation(entrySignal);
            Session capturedSession = pane.Session;
            long? observedArrangementVersion = activity.ArrangementVersion(pane);
            bool placementLost = false;
            string? originalPid = null;
            int matchingWork = 0;
            PaneTextGridCursor? cursor = null;

            async Task CheckPlacementAsync()
            {
                if ((await capturedSession.GetPanesAsync(token).ConfigureAwait(false))
                    .Any(candidate => candidate.Id == pane.Id))
                {
                    return;
                }

                if (!activity.AllowsPollingFallback)
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

            async Task<PaneTextWaitResult> FinishAsync(
                PaneTextWaitOutcome outcome,
                string? matchedPattern)
            {
                if (outcome != PaneTextWaitOutcome.TimedOut)
                {
                    confirmedOutcome = outcome;
                    confirmedPattern = matchedPattern;
                }

                while (true)
                {
                    object? finalSignal = null;
                    if (outcome == PaneTextWaitOutcome.TimedOut)
                    {
                        finalSignal = placementLost ? null : activity.CaptureSignal(pane);
                        await RecheckArrangementAsync().ConfigureAwait(false);
                        if (placementLost)
                        {
                            finalSignal = null;
                        }

                        pollingFallback |= activity.RequireObservation(finalSignal);
                    }

                    PaneTextGridRead? final = null;
                    try
                    {
                        final = await PaneTextGridReader.ReadVisibleAsync(
                                pane, originalPid, token)
                            .ConfigureAwait(false);
                        lastTail = final.Lines;
                    }
                    catch (Exception error) when (error is PaneTextGridReader.PaneGoneException
                        or PaneTextGridReader.PaneReplacedException)
                    {
                        if (outcome == PaneTextWaitOutcome.TimedOut)
                        {
                            outcome = PaneTextWaitOutcome.PaneDied;
                        }
                    }
                    catch (PaneTextGridReader.UnstableSnapshotException)
                    {
                        // The last stable read is a better result than an unstable final grid.
                    }

                    if (outcome == PaneTextWaitOutcome.TimedOut && final is not null)
                    {
                        await RecheckArrangementAsync().ConfigureAwait(false);
                        if (placementLost)
                        {
                            finalSignal = null;
                        }

                        IReadOnlyList<string> visible = ProjectMatch(final.Lines);
                        if (Match(options.Stops, visible, ref matchingWork, token) is string stopped)
                        {
                            outcome = PaneTextWaitOutcome.Stopped;
                            matchedPattern = stopped;
                        }
                        else if (Match(options.Wanted, visible, ref matchingWork, token) is string hit)
                        {
                            outcome = PaneTextWaitOutcome.Matched;
                            matchedPattern = hit;
                        }
                        else if (options.Wanted.Length == 0 && cursor is not null)
                        {
                            try
                            {
                                PaneTextGridRead delta = await PaneTextGridReader.ReadSinceAsync(
                                        pane, cursor, token)
                                    .ConfigureAwait(false);
                                linesMissed |= delta.LinesMissed;
                                anchorLost |= delta.AnchorLost;
                                if (!delta.LinesMissed && ProjectMatch(delta.Lines).Count > 0)
                                {
                                    lastTail = delta.Lines;
                                    outcome = PaneTextWaitOutcome.AnyOutput;
                                }
                            }
                            catch (Exception error) when (error is PaneTextGridReader.PaneGoneException
                                or PaneTextGridReader.PaneReplacedException)
                            {
                                outcome = PaneTextWaitOutcome.PaneDied;
                            }
                            catch (PaneTextGridReader.UnstableSnapshotException)
                            {
                                linesMissed = true;
                            }
                        }
                        if (outcome == PaneTextWaitOutcome.TimedOut && final.State.Dead)
                        {
                            outcome = PaneTextWaitOutcome.PaneDied;
                        }
                    }

                    if (outcome != PaneTextWaitOutcome.TimedOut)
                    {
                        break;
                    }

                    TimeSpan remaining = options.Timeout - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    bool changed = await activity.WaitForActivityAsync(
                            pane.Id.ToString(), finalSignal, remaining, token,
                            TimeProvider.System)
                        .ConfigureAwait(false);
                    if (!changed && finalSignal is Task)
                    {
                        break;
                    }
                }

                IReadOnlyList<string> projected = tailLines?.Invoke(lastTail) ?? lastTail;
                BoundedTail bounded = BoundTail(projected, options.TailLines, options.MaxOutputBytes);
                return new PaneTextWaitResult(
                    pane.Id,
                    outcome,
                    matchedPattern,
                    bounded.Lines,
                    elapsed.Elapsed,
                    options.Timeout,
                    PaneActivitySource.EventsDropped(lease),
                    pollingFallback,
                    pollingFallback ? PaneActivitySource.PollInterval : null,
                    linesMissed || PaneActivitySource.EventsDropped(lease) > 0,
                    anchorLost,
                    bounded.OmittedLines,
                    bounded.OmittedBytes);
            }

            IReadOnlyList<string> ProjectMatch(IReadOnlyList<string> lines) =>
                matchLines?.Invoke(lines) ?? lines;

            PaneTextGridRead first;
            try
            {
                first = await PaneTextGridReader.ReadVisibleAsync(pane, null, token)
                    .ConfigureAwait(false);
            }
            catch (PaneTextGridReader.PaneGoneException)
            {
                return await FinishAsync(PaneTextWaitOutcome.PaneDied, null).ConfigureAwait(false);
            }

            originalPid = first.State.PanePid;
            lastTail = first.Lines;
            await RecheckArrangementAsync().ConfigureAwait(false);
            if (first.State.Dead)
            {
                return await FinishAsync(PaneTextWaitOutcome.PaneDied, null).ConfigureAwait(false);
            }

            IReadOnlyList<string> visibleAtEntry = ProjectMatch(first.Lines);
            if (Match(options.Stops, visibleAtEntry, ref matchingWork, token) is string stoppedAtEntry)
            {
                return await FinishAsync(PaneTextWaitOutcome.Stopped, stoppedAtEntry)
                    .ConfigureAwait(false);
            }

            if (Match(options.Wanted, visibleAtEntry, ref matchingWork, token) is string present)
            {
                return await FinishAsync(PaneTextWaitOutcome.PresentAtEntry, present)
                    .ConfigureAwait(false);
            }

            cursor = PaneTextGridCursor.Build(first.State, first.CursorRows);
            while (true)
            {
                TimeSpan remaining = observationWindow - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return await FinishAsync(PaneTextWaitOutcome.TimedOut, null).ConfigureAwait(false);
                }

                // Capture the wake token before reading: a concurrent output event
                // must wake the wait even if the capture did not include its text.
                object? signal = placementLost ? null : activity.CaptureSignal(pane);
                await RecheckArrangementAsync().ConfigureAwait(false);
                if (placementLost)
                {
                    signal = null;
                }

                pollingFallback |= activity.RequireObservation(signal);
                PaneTextGridRead read;
                try
                {
                    read = await PaneTextGridReader.ReadSinceAsync(pane, cursor, token)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (error is PaneTextGridReader.PaneGoneException
                    or PaneTextGridReader.PaneReplacedException)
                {
                    return await FinishAsync(PaneTextWaitOutcome.PaneDied, null).ConfigureAwait(false);
                }
                catch (PaneTextGridReader.UnstableSnapshotException)
                {
                    progress?.Invoke(elapsed.Elapsed, options.Timeout, $"waiting on {pane.Id}");
                    bool changed = await activity.WaitForActivityAsync(
                            pane.Id.ToString(), signal, observationWindow - elapsed.Elapsed,
                            token)
                        .ConfigureAwait(false);
                    if (!changed && signal is Task)
                    {
                        return await FinishAsync(PaneTextWaitOutcome.TimedOut, null)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                cursor = PaneTextGridCursor.Build(read.State, read.CursorRows);
                await RecheckArrangementAsync().ConfigureAwait(false);
                linesMissed |= read.LinesMissed;
                anchorLost |= read.AnchorLost;
                if (read.Lines.Count > 0)
                {
                    lastTail = read.Lines;
                }

                IReadOnlyList<string> visible = ProjectMatch(read.Lines);
                if (Match(options.Stops, visible, ref matchingWork, token) is string stopped)
                {
                    return await FinishAsync(PaneTextWaitOutcome.Stopped, stopped)
                        .ConfigureAwait(false);
                }

                if (visible.Count > 0 && options.Wanted.Length == 0)
                {
                    return await FinishAsync(PaneTextWaitOutcome.AnyOutput, null)
                        .ConfigureAwait(false);
                }

                if (Match(options.Wanted, visible, ref matchingWork, token) is string matched)
                {
                    return await FinishAsync(PaneTextWaitOutcome.Matched, matched)
                        .ConfigureAwait(false);
                }

                if (read.State.Dead)
                {
                    return await FinishAsync(PaneTextWaitOutcome.PaneDied, null)
                        .ConfigureAwait(false);
                }

                progress?.Invoke(
                    elapsed.Elapsed, options.Timeout,
                    read.Lines.Count > 0 ? read.Lines[^1] : $"waiting on {pane.Id}");

                bool activityObserved = await activity.WaitForActivityAsync(
                        pane.Id.ToString(), signal, observationWindow - elapsed.Elapsed,
                        token)
                    .ConfigureAwait(false);
                if (!activityObserved && signal is Task)
                {
                    return await FinishAsync(PaneTextWaitOutcome.TimedOut, null)
                        .ConfigureAwait(false);
                }
            }

            async Task RecheckArrangementAsync()
            {
                long? currentArrangementVersion = activity.ArrangementVersion(pane);
                if (!placementLost && currentArrangementVersion is not null
                    && currentArrangementVersion != observedArrangementVersion)
                {
                    observedArrangementVersion = currentArrangementVersion;
                    await CheckPlacementAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested
            && !activity.LifetimeToken.IsCancellationRequested)
        {
            IReadOnlyList<string> projected = tailLines?.Invoke(lastTail) ?? lastTail;
            BoundedTail bounded = BoundTail(projected, options.TailLines, options.MaxOutputBytes);
            long dropped = lease is null ? 0 : PaneActivitySource.EventsDropped(lease);
            return new PaneTextWaitResult(
                pane.Id,
                confirmedOutcome ?? PaneTextWaitOutcome.TimedOut,
                confirmedPattern,
                bounded.Lines,
                elapsed.Elapsed,
                options.Timeout,
                dropped,
                pollingFallback,
                pollingFallback ? PaneActivitySource.PollInterval : null,
                linesMissed || dropped > 0,
                anchorLost,
                bounded.OmittedLines,
                bounded.OmittedBytes);
        }
    }

    internal static void ValidateRequest(PaneTextWaitRequest request) => _ = Validate(request);

    private static ValidatedRequest Validate(PaneTextWaitRequest request)
    {
        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The pane wait timeout must be positive.");
        }

        if (request.TailLines is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "TailLines must be between 1 and 1000.");
        }

        if (request.MaxOutputBytes is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "MaxOutputBytes must be between 1 and 1048576.");
        }

        string[] wanted = request.Patterns?.ToArray() ?? [];
        string[] stops = request.StopPatterns?.ToArray() ?? [];
        if (wanted.Length + stops.Length > PaneTextWaitRequest.MaximumPatterns)
        {
            throw new ArgumentException(
                $"A pane wait accepts at most {PaneTextWaitRequest.MaximumPatterns} patterns.",
                nameof(request));
        }

        int totalBytes = 0;
        foreach (string? pattern in wanted.Concat(stops))
        {
            if (string.IsNullOrEmpty(pattern))
            {
                throw new ArgumentException("A pane wait pattern cannot be empty.", nameof(request));
            }

            int bytes = Encoding.UTF8.GetByteCount(pattern);
            if (bytes > MaximumPatternBytes || bytes > MaximumPatternBytesTotal - totalBytes)
            {
                throw new ArgumentException(
                    $"Pane wait patterns may use at most {MaximumPatternBytes} UTF-8 bytes each "
                    + $"and {MaximumPatternBytesTotal} bytes in total.", nameof(request));
            }

            totalBytes += bytes;
        }

        return new ValidatedRequest(
            Compile(wanted, request.IgnoreCase, request.SimpleMatch),
            Compile(stops, request.IgnoreCase, request.SimpleMatch),
            request.Timeout > MaximumTimeout ? MaximumTimeout : request.Timeout,
            request.TailLines,
            request.MaxOutputBytes);
    }

    private static Pattern[] Compile(string[] values, bool ignoreCase, bool simpleMatch)
    {
        RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
            | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return [.. values.Select(value => new Pattern(
            value,
            simpleMatch ? null : new Regex(value, options, TimeSpan.FromSeconds(1)),
            ignoreCase))];
    }

    private static string? Match(
        Pattern[] patterns,
        IReadOnlyList<string> lines,
        ref int matchingWork,
        CancellationToken cancellationToken)
    {
        foreach (Pattern pattern in patterns)
        {
            foreach (string line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int bytes = Encoding.UTF8.GetByteCount(line);
                if (bytes > MaximumMatchWorkBytes - matchingWork)
                {
                    throw new MatchWorkExceededException();
                }

                matchingWork += bytes;
                if (pattern.Matches(line))
                {
                    return pattern.Text;
                }
            }
        }

        return null;
    }

    private static BoundedTail BoundTail(
        IReadOnlyList<string> lines,
        int maxLines,
        int maxBytes)
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

    private sealed record Pattern(string Text, Regex? Regex, bool IgnoreCase)
    {
        internal bool Matches(string line) => Regex?.IsMatch(line)
            ?? line.Contains(
                Text,
                IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private sealed record ValidatedRequest(
        Pattern[] Wanted,
        Pattern[] Stops,
        TimeSpan Timeout,
        int TailLines,
        int MaxOutputBytes);

    private sealed record BoundedTail(IReadOnlyList<string> Lines, int OmittedLines, int OmittedBytes);

    internal sealed class MatchWorkExceededException()
        : IOException("Pane wait matching work exceeded 8388608 UTF-8 bytes.");
}
