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
    /// <see cref="PaneWaitOutcome.PresentAtEntry" />. While the wait runs, a
    /// control-mode client attaches with <c>ignore-size</c> and shows in
    /// <c>list-clients</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">The text is empty.</exception>
    /// <exception cref="TmuxPaneException">The pane's program had already exited, or the pane closed.</exception>
    [UnsupportedOSPlatform("windows")]
    public Task<PaneWaitResult> WaitForTextAsync(
        string text,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return WaitForTextAsync(
            new PaneWaitRequest { Patterns = [new Regex(Regex.Escape(text), RegexOptions.CultureInvariant)], Timeout = timeout },
            cancellationToken);
    }

    /// <summary>Waits until the pane prints output a request describes.</summary>
    /// <param name="request">The patterns and time allowed.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>How the wait ended.</returns>
    /// <remarks>
    /// The wait sleeps on the pane's output through a control-mode client,
    /// which attaches with <c>ignore-size</c> and shows in
    /// <c>list-clients</c> while any wait on its session runs.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative.</exception>
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
