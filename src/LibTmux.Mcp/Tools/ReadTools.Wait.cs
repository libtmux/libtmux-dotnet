using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibTmux.Internal;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <content>Waiting for a pane to say something, without polling it.</content>
[UnsupportedOSPlatform("windows")]
internal sealed partial class ReadTools
{
    /// <summary>Waits until a pane prints text a caller is looking for.</summary>
    /// <param name="paneId">The pane, or null for the caller's pane or the one the first session shows.</param>
    /// <param name="patterns">What to wait for, or null for any output at all.</param>
    /// <param name="stopPatterns">What means waiting is pointless.</param>
    /// <param name="timeoutSeconds">How long to wait, before the server's ceiling.</param>
    /// <param name="ignoreCase">Whether case is ignored.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="progress">Reports that the wait is still running.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>How the wait ended and what the pane showed.</returns>
    /// <remarks>
    /// For a command the caller wrote, <c>run_shell_command</c> is better: it knows
    /// exactly when the command finished and what it exited with, where this
    /// can only recognise text. This is for output nobody here authored — a
    /// server starting up, a build another process launched, a person typing.
    /// </remarks>
    [Description(
        "Wait until a pane prints something matching one of these patterns, then "
        + "return. Use for output you did NOT start — a server's ready line, another "
        + "process's progress, a person typing. For a command you are running "
        + "yourself, run_shell_command is better: it reports the real exit status instead of "
        + "guessing from text. Omit patterns to wait for any new output at all. "
        + "Never poll capture_pane in a loop; this call does the waiting.")]
    public async Task<WaitResult> WaitForTextAsync(
        [Description(
            "The pane id, such as %1. Omit for this server's own pane, or else the one the "
            + "first session shows.")]
        string? paneId = null,
        [Description(
            "Regular expressions to wait for. A pattern already on screen when this is "
            + "called is answered at once as outcome PresentAtEntry, not Timeout, so a "
            + "tail that contains it is never reported as a plain timeout. Text this "
            + "server itself typed into the pane is never a match by itself, however new "
            + "tmux reports it — a not-yet-submitted line, a shell re-printing what was "
            + "typed, or a line just submitted, stay discounted for a few seconds after "
            + "the edit or the submit, so they cannot satisfy a wait meant for the pane's "
            + "own output. This cannot protect a pane whose program has not yet configured "
            + "its terminal — wait for a first prompt before typing into a freshly created "
            + "pane. Omit or pass an empty list to return as soon as the pane prints "
            + "anything new. Across both pattern lists: at most 32 entries and 16384 UTF-8 "
            + "bytes; each entry is at most 999 bytes.")]
        IReadOnlyList<string>? patterns = null,
        [Description(
            "Regular expressions meaning the thing you are waiting for will never "
            + "come, such as an error line. Matching one ends the wait as 'stopped'. "
            + "A stop pattern wins over a wanted pattern on the same screen, including "
            + "the initial screen. It shares the patterns count and byte limits.")]
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
        TimeSpan budget = _policy.EffectiveTimeout(
            timeoutSeconds is double seconds ? TimeSpan.FromSeconds(seconds) : null);
        PaneWaitRequest request;
        try
        {
            request = PaneWaitRequest.FromTextPatterns(patterns, stopPatterns, ignoreCase) with
            {
                Timeout = budget,
                AllowPollingFallback = _policy.AllowPollingFallback,
                TailLines = TailLines,
                MaxOutputBytes = Math.Min(_policy.MaxBytes, 1_048_576),
            };
            request.Validate();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new McpException($"Invalid pane wait: {error.Message}", error);
        }

        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);

        // Own echoes must not satisfy a wait for another process's output.
        // Keep observed recent lines for this wait even after their registry TTL expires.
        var recentSoFar = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<string> Discounted(IReadOnlyList<string> lines)
        {
            PaneEchoRegistry.LiveEcho echo = PaneEchoRegistry.GetLiveEcho(pane);
            foreach (string line in echo.Recent)
            {
                recentSoFar.Add(line);
            }

            if (echo.Pending.Length == 0 && recentSoFar.Count == 0)
            {
                return lines;
            }

            IEnumerable<string> echoes = echo.Pending.Length == 0
                ? recentSoFar
                : recentSoFar.Prepend(echo.Pending);
            return [.. lines
                .Select(line => PaneText.WithoutEchoes(line, echoes))
                .Where(masked => masked.Length > 0)];
        }

        PaneWaitResult observed;
        try
        {
            observed = await PaneTextWaiter.WaitAsync(
                    _activity,
                    pane,
                    request.Snapshot(),
                    matchLines: lines => Discounted(PaneText.Scrub(lines, pane.Width)),
                    tailLines: lines => PaneText.Scrub(lines, pane.Width),
                    progress: (elapsed, effective, message) =>
                        Report(progress, elapsed, effective, message),
                    cancellationToken: cancellationToken,
                    // An unmodelled key settles its echo record only after dispatch.
                    // Process reads narrow the race with that record's settlement.
                    readThroughControl: false)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is RegexMatchTimeoutException
            or PaneTextWaiter.MatchWorkExceededException)
        {
            throw WaitMatchingError(error);
        }
        WaitOutcome outcome = observed.Outcome switch
        {
            PaneWaitOutcome.PresentAtEntry => WaitOutcome.PresentAtEntry,
            PaneWaitOutcome.Matched => WaitOutcome.Matched,
            PaneWaitOutcome.Stopped => WaitOutcome.Stopped,
            PaneWaitOutcome.AnyOutput => WaitOutcome.AnyOutput,
            PaneWaitOutcome.TimedOut => WaitOutcome.Timeout,
            PaneWaitOutcome.PaneExited => WaitOutcome.PaneDied,
            _ => throw new InvalidOperationException("Unknown pane wait outcome."),
        };

        return StructuredTextResultBudget.Fit(
            observed.Tail,
            TailLines,
            _policy.MaxBytes,
            content => new WaitResult(
                observed.PaneId.ToString(),
                outcome,
                observed.Pattern,
                content,
                Math.Round(observed.Elapsed.TotalSeconds, 3),
                observed.EffectiveTimeout.TotalSeconds)
            {
                PollingFallback = observed.PollingFallback,
                EventsDropped = observed.EventsDropped,
                LinesMissed = observed.LinesMissed,
                AnchorLost = observed.AnchorLost,
            },
            "pane wait");
    }

    internal static McpException WaitMatchingError(Exception error) => error switch
    {
        RegexMatchTimeoutException timedOut => new McpException(
            $"The pattern '{timedOut.Pattern}' took too long to match. Simplify it.",
            timedOut),
        PaneTextWaiter.MatchWorkExceededException exceeded => new McpException(
            "Pane wait matching work limit exceeded; use fewer patterns or a narrower pane.",
            exceeded),
        _ => throw new ArgumentException("Not a pane wait matching failure.", nameof(error)),
    };


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

    internal static void ValidateWaitPatterns(
        IReadOnlyList<string>? patterns,
        IReadOnlyList<string>? stopPatterns,
        int resultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultMaxBytes);
        long count = (patterns?.Count ?? 0L) + (stopPatterns?.Count ?? 0L);
        if (count > PaneWaitRequest.MaximumPatterns)
        {
            throw new McpException(
                $"A pane wait accepts at most {PaneWaitRequest.MaximumPatterns} patterns across both lists.");
        }

        try
        {
            PaneWaitRequest.FromTextPatterns(patterns, stopPatterns).Validate();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new McpException(error.Message, error);
        }

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

    }


    /// <summary>How much of the pane a wait reports back when it ends.</summary>
    /// <remarks>
    /// Enough to see what happened, not enough to be a capture. A caller who
    /// wants the pane can read it; a caller who does not should not pay for it.
    /// </remarks>
    private const int TailLines = 20;
}
