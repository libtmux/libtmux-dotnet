using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibTmux.Internal;

namespace LibTmux;

// Waits on a pane's output without polling it.
public sealed partial class Pane
{
    /// <summary>Waits until the pane shows literal text.</summary>
    /// <param name="text">The text to wait for, matched within a line.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>How the wait ended.</returns>
    /// <remarks>
    /// Text already on screen ends the wait at once as
    /// <see cref="PaneWaitOutcome.PresentAtEntry" />. Matching is line by line,
    /// so the text cannot span lines.
    /// </remarks>
    /// <exception cref="ArgumentException">The text is empty or contains a line break.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative or longer than 49 days.</exception>
    /// <exception cref="TmuxPaneException">The pane's program had already exited, or the pane closed.</exception>
    [UnsupportedOSPlatform("windows")]
    public Task<PaneWaitResult> WaitForTextAsync(
        string text,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (text.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException("Matching is line by line, so the text cannot contain a line break.", nameof(text));
        }

        return WaitForTextAsync(
            new PaneWaitRequest { Patterns = [new Regex(Regex.Escape(text), RegexOptions.CultureInvariant)], Timeout = timeout },
            cancellationToken);
    }

    /// <summary>Waits until the pane prints output a request describes.</summary>
    /// <param name="request">The patterns and time allowed.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>How the wait ended.</returns>
    /// <remarks>
    /// <para>
    /// The wait sleeps on the pane's output through a control-mode client. It
    /// attaches with <c>ignore-size</c> while any wait on its session runs, so
    /// it shows in <c>list-clients</c> and counts in <c>session_attached</c>.
    /// </para>
    /// <para>
    /// A pane whose program exits or closes during the wait ends it as
    /// <see cref="PaneWaitOutcome.PaneExited" />; one that had already exited
    /// raises <see cref="TmuxPaneException" />.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative or longer than 49 days.</exception>
    /// <exception cref="TmuxPaneException">The pane's program had already exited, or the pane closed.</exception>
    /// <exception cref="RegexMatchTimeoutException">A pattern exceeded its own match timeout.</exception>
    [UnsupportedOSPlatform("windows")]
    public Task<PaneWaitResult> WaitForTextAsync(
        PaneWaitRequest request,
        CancellationToken cancellationToken = default) =>
        WaitForTextAsync(request, progress: null, cancellationToken);

    [UnsupportedOSPlatform("windows")]
    internal async Task<PaneWaitResult> WaitForTextAsync(
        PaneWaitRequest request,
        Action<TimeSpan, string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Timeout, PaneTextWaiter.LongestTimeout);
        Regex[] wanted = [.. request.Patterns];
        Regex[] stops = [.. request.StopPatterns];

        PaneWaitVerdict? Classify(IReadOnlyList<string> lines, bool atEntry)
        {
            if (atEntry)
            {
                return Match(wanted, lines) is { } present
                    ? new(PaneWaitOutcome.PresentAtEntry, present)
                    : null;
            }

            if (Match(stops, lines) is { } stopped)
            {
                return new(PaneWaitOutcome.Stopped, stopped);
            }

            if (wanted.Length == 0)
            {
                return new(PaneWaitOutcome.AnyOutput, null);
            }

            return Match(wanted, lines) is { } matched ? new(PaneWaitOutcome.Matched, matched) : null;
        }

        (PaneWaitOutcome outcome, string? pattern, TimeSpan elapsed) = await PaneTextWaiter
            .WaitAsync(
                this,
                PaneActivityHub.Shared,
                Classify,
                request.Timeout,
                PaneReader.Failure,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        return new PaneWaitResult(outcome, pattern, elapsed);
    }

    /// <summary>Waits until a condition holds over the rows the pane shows.</summary>
    /// <param name="condition">Judges the visible rows, top to bottom.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>How the wait ended.</returns>
    /// <remarks>
    /// The condition is tested when the wait begins and again each time the
    /// pane prints or changes state, so a screen that is already right ends the
    /// wait at once as <see cref="PaneWaitOutcome.PresentAtEntry" />. Unlike
    /// <see cref="WaitForTextAsync(PaneWaitRequest, CancellationToken)" />, it
    /// sees the whole screen, including rows a full-screen program redraws.
    /// A program that exits leaving a screen the condition accepts ends the
    /// wait as <see cref="PaneWaitOutcome.Matched" />, not
    /// <see cref="PaneWaitOutcome.PaneExited" />.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative or longer than 49 days.</exception>
    /// <exception cref="TmuxPaneException">The pane's program had already exited, or another program replaced it during the wait.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<PaneWaitResult> WaitUntilAsync(
        Func<IReadOnlyList<string>, bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, PaneTextWaiter.LongestTimeout);
        (PaneWaitOutcome outcome, TimeSpan elapsed) = await PaneTextWaiter
            .WaitForScreenAsync(this, PaneActivityHub.Shared, condition, timeout, PaneReader.Failure, cancellationToken)
            .ConfigureAwait(false);
        return new PaneWaitResult(outcome, null, elapsed);
    }

    private static string? Match(Regex[] patterns, IReadOnlyList<string> lines)
    {
        foreach (Regex pattern in patterns)
        {
            foreach (string line in lines)
            {
                if (pattern.IsMatch(line))
                {
                    return pattern.ToString();
                }
            }
        }

        return null;
    }
}
