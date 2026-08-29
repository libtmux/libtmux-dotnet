using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LibTmux.Mcp;

/// <content>Waiting for a pane to say something, without polling it.</content>
[UnsupportedOSPlatform("windows")]
public sealed partial class ReadTools
{
    /// <summary>Waits until a pane prints text a caller is looking for.</summary>
    /// <param name="paneId">The pane, or null for the active one.</param>
    /// <param name="patterns">What to wait for, or null for any output at all.</param>
    /// <param name="stopPatterns">What means waiting is pointless.</param>
    /// <param name="timeoutSeconds">How long to wait, before the server's ceiling.</param>
    /// <param name="ignoreCase">Whether case is ignored.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="progress">Reports that the wait is still running.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>How the wait ended and what the pane showed.</returns>
    /// <remarks>
    /// For a command the caller wrote, <c>tmux_run</c> is better: it knows
    /// exactly when the command finished and what it exited with, where this
    /// can only recognise text. This is for output nobody here authored — a
    /// server starting up, a build another process launched, a person typing.
    /// </remarks>
    [McpServerTool(Name = "tmux_wait_for_text", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Wait until a pane prints something matching one of these patterns, then "
        + "return. Use for output you did NOT start — a server's ready line, another "
        + "process's progress, a person typing. For a command you are running "
        + "yourself, tmux_run is better: it reports the real exit status instead of "
        + "guessing from text. Omit patterns to wait for any new output at all. "
        + "Never poll tmux_capture_pane in a loop; this call does the waiting.")]
    public async Task<WaitResult> WaitForTextAsync(
        [Description("The pane id, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description(
            "Regular expressions to wait for. Omit or pass an empty list to return as "
            + "soon as the pane prints anything new. Across both pattern lists: at most "
            + "32 entries and 16384 UTF-8 bytes; each entry is at most 4096 bytes.")]
        IReadOnlyList<string>? patterns = null,
        [Description(
            "Regular expressions meaning the thing you are waiting for will never "
            + "come, such as an error line. Matching one ends the wait as 'stopped'. "
            + "It shares the patterns count and byte limits.")]
        IReadOnlyList<string>? stopPatterns = null,
        [Description(
            "Seconds to wait. Lowered to the server's ceiling; read "
            + "effectiveTimeoutSeconds for the value actually used.")]
        double? timeoutSeconds = null,
        [Description("Ignore case when matching.")] bool ignoreCase = true,
        [Description("The tmux socket to read. Omit for the default server.")]
        string? socketName = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateWaitPatterns(patterns, stopPatterns, _policy.MaxBytes);
        Regex[] wanted = Compile(patterns, ignoreCase);
        Regex[] stops = Compile(stopPatterns, ignoreCase);
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        string id = pane.Id.ToString();
        TimeSpan budget = _policy.EffectiveTimeout(
            timeoutSeconds is double seconds ? TimeSpan.FromSeconds(seconds) : null);

        Stopwatch elapsed = Stopwatch.StartNew();

        // The lease turns this from a poll into a sleep: tmux reports the
        // pane's output as it happens, and the loop below wakes on it.
        IAsyncDisposable lease = await _activity.WatchAsync(pane, cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable _ = lease.ConfigureAwait(false);

        PaneRead first = await PaneReader.ReadVisibleAsync(pane, null, cancellationToken)
            .ConfigureAwait(false);
        TailCursor cursor = TailCursor.Build(pane, first.State, first.CursorRows);
        bool alternate = first.State.AlternateScreen;

        while (true)
        {
            TimeSpan remaining = budget - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return await FinishAsync(
                        pane,
                        id,
                        WaitOutcome.Timeout,
                        null,
                        elapsed,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // Taken before the read, so output arriving during the read wakes
            // the next wait instead of being slept through.
            object? signal = _activity.CaptureSignal(pane);

            PaneRead read = await PaneReader.ReadSinceAsync(pane, cursor, cancellationToken)
                .ConfigureAwait(false);
            cursor = TailCursor.Build(pane, read.State, read.CursorRows);

            if (read.Lines.Count > 0)
            {
                if (Match(stops, read.Lines, cancellationToken) is string stopped)
                {
                    return await FinishAsync(
                            pane,
                            id,
                            WaitOutcome.Stopped,
                            stopped,
                            elapsed,
                            budget,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (wanted.Length == 0)
                {
                    return await FinishAsync(
                            pane,
                            id,
                            WaitOutcome.AnyOutput,
                            null,
                            elapsed,
                            budget,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (Match(wanted, read.Lines, cancellationToken) is string hit)
                {
                    return await FinishAsync(
                            pane,
                            id,
                            WaitOutcome.Matched,
                            hit,
                            elapsed,
                            budget,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (read.State.Dead)
            {
                return await FinishAsync(
                        pane,
                        id,
                        WaitOutcome.PaneDied,
                        null,
                        elapsed,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // A full-screen program repaints rather than appending, so "what is
            // new" stops meaning anything. Saying so beats waiting out the
            // whole budget for a line that will never arrive as new text.
            if (!alternate && read.State.AlternateScreen)
            {
                return await FinishAsync(
                        pane,
                        id,
                        WaitOutcome.Timeout,
                        null,
                        elapsed,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // Told, rather than left silent. A client showing a wait needs to
            // know it is still running; without this a thirty second wait is
            // indistinguishable from a hung one.
            Report(
                progress,
                elapsed.Elapsed,
                budget,
                read.Lines.Count > 0 ? read.Lines[^1] : $"waiting on {id}");

            await _activity.WaitForActivityAsync(
                    id,
                    signal,
                    budget - elapsed.Elapsed,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }


    /// <summary>Tells the client a wait is still running.</summary>
    /// <param name="progress">Where to report, or null when the client asked for none.</param>
    /// <param name="elapsed">How long the wait has run.</param>
    /// <param name="budget">How long it may run.</param>
    /// <param name="message">What the pane last showed.</param>
    internal static void Report(
        IProgress<ProgressNotificationValue>? progress,
        TimeSpan elapsed,
        TimeSpan budget,
        string message)
    {
        progress?.Report(new ProgressNotificationValue
        {
            Progress = (float)elapsed.TotalSeconds,
            Total = (float)budget.TotalSeconds,
            Message = message.Length <= 120 ? message : message[..120],
        });
    }

    private static Regex[] Compile(IReadOnlyList<string>? patterns, bool ignoreCase) =>
        patterns is null
            ? []
            : [.. patterns
                .Where(each => !string.IsNullOrEmpty(each))
                .Select(each => CompilePattern(each, ignoreCase))];

    internal static void ValidateWaitPatterns(
        IReadOnlyList<string>? patterns,
        IReadOnlyList<string>? stopPatterns,
        int resultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultMaxBytes);
        long count = (patterns?.Count ?? 0L) + (stopPatterns?.Count ?? 0L);
        if (count > MaximumWaitPatterns)
        {
            throw new McpException(
                $"A pane wait accepts at most {MaximumWaitPatterns} patterns across both lists.");
        }

        int totalBytes = 0;
        foreach (IReadOnlyList<string>? list in new[] { patterns, stopPatterns })
        {
            if (list is null)
            {
                continue;
            }

            foreach (string? pattern in list)
            {
                if (string.IsNullOrEmpty(pattern))
                {
                    continue;
                }

                if (pattern.Length > MaximumWaitPatternBytes)
                {
                    throw PatternBudgetError();
                }

                int bytes = Encoding.UTF8.GetByteCount(pattern);
                if (bytes > MaximumWaitPatternBytes
                    || bytes > MaximumWaitPatternBytesTotal - totalBytes)
                {
                    throw PatternBudgetError();
                }

                totalBytes += bytes;
                var probe = new WaitResult(
                    "%18446744073709551615",
                    WaitOutcome.Matched,
                    pattern,
                    BoundedText.Empty,
                    double.MaxValue,
                    double.MaxValue);
                if (Utf8JsonBudget.GetStructuredToolResultByteCount(probe, ToolJson.Options)
                    > resultMaxBytes)
                {
                    throw new McpException(
                        "A wait pattern cannot fit in the configured result byte ceiling. "
                        + $"Use a shorter pattern or raise {ServerPolicy.MaxBytesVariable}.");
                }
            }
        }

        static McpException PatternBudgetError() => new(
            $"Pane-wait patterns may use at most {MaximumWaitPatternBytes} UTF-8 bytes each "
            + $"and {MaximumWaitPatternBytesTotal} bytes across both lists.");
    }


    internal static string? Match(
        Regex[] patterns,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default)
    {
        foreach (Regex pattern in patterns)
        {
            foreach (string line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (pattern.IsMatch(line))
                    {
                        return pattern.ToString();
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    throw new McpException(
                        $"The pattern '{pattern}' took too long to match. Simplify it — "
                        + "nested quantifiers such as (a+)+ backtrack badly on terminal text.");
                }
            }
        }

        return null;
    }

    private async Task<WaitResult> FinishAsync(
        Pane pane,
        string paneId,
        WaitOutcome outcome,
        string? matched,
        Stopwatch elapsed,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> tail = await PaneReader.CaptureAsync(pane, null, cancellationToken)
            .ConfigureAwait(false);
        double elapsedSeconds = Math.Round(elapsed.Elapsed.TotalSeconds, 3);
        return StructuredTextResultBudget.Fit(
            PaneText.Scrub(tail, pane.Width),
            TailLines,
            _policy.MaxBytes,
            content => new WaitResult(
                paneId,
                outcome,
                matched,
                content,
                elapsedSeconds,
                budget.TotalSeconds),
            "pane wait");
    }

    /// <summary>How much of the pane a wait reports back when it ends.</summary>
    /// <remarks>
    /// Enough to see what happened, not enough to be a capture. A caller who
    /// wants the pane can read it; a caller who does not should not pay for it.
    /// </remarks>
    private const int TailLines = 20;
    private const int MaximumWaitPatterns = 32;
    private const int MaximumWaitPatternBytes = 4_096;
    private const int MaximumWaitPatternBytesTotal = 16_384;
}
