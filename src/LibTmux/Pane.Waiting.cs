using System.Runtime.CompilerServices;
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
    /// <exception cref="TmuxPaneException">The pane's program had already exited.</exception>
    /// <exception cref="TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    [UnsupportedOSPlatform("windows")]
    public Task<PaneWaitResult> WaitForTextAsync(
        string text,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        WaitForTextAsync(new PaneWaitRequest { Patterns = [Containing(text)], Timeout = timeout }, cancellationToken);

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
    /// <exception cref="TmuxPaneException">The pane's program had already exited.</exception>
    /// <exception cref="TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
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

    /// <summary>Types a line, presses Enter, and waits for a later line to contain the text.</summary>
    /// <param name="line">The line to type, sent verbatim.</param>
    /// <param name="text">The text to wait for, matched literally.</param>
    /// <param name="timeout">How long to wait, counted from the call.</param>
    /// <param name="cancellationToken">Stops the wait; a line already sent stays sent.</param>
    /// <returns>How the wait ended.</returns>
    /// <remarks>
    /// <see cref="SendKeysAndWaitAsync" /> with <see cref="SendTextAsync" />'s
    /// keys: the screen before the line is typed never ends the wait, and the
    /// line's echo is discounted.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="line" /> is null.</exception>
    /// <exception cref="ArgumentException">The text is empty or spans lines.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative or longer than 49 days.</exception>
    /// <exception cref="TmuxPaneException">The pane's program had already exited.</exception>
    /// <exception cref="TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    /// <exception cref="LibTmuxException">
    /// The line was typed but Enter failed. The pane may already have acted
    /// on it; do not retry the whole call.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Task<PaneWaitResult> SendTextAndWaitAsync(
        string line,
        string text,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        return SendKeysAndWaitAsync(
            new SendKeysRequest { Text = line, Literal = true },
            new PaneWaitRequest { Patterns = [Containing(text)], Timeout = timeout },
            cancellationToken);
    }

    /// <summary>Sends keys to the pane and waits for what it prints in response.</summary>
    /// <param name="keys">What to type.</param>
    /// <param name="wait">The patterns that end the wait, and how long to wait.</param>
    /// <param name="cancellationToken">Stops the wait; keys already sent stay sent.</param>
    /// <returns>How the wait ended.</returns>
    /// <remarks>
    /// <para>
    /// The wait reads the screen before the keys go, so output that follows at
    /// once is not missed, and judges only what the pane prints afterwards:
    /// text already on screen never ends it. A shell echoes typed text back, so
    /// literal text (<see cref="SendKeysRequest.Literal" />, as
    /// <see cref="SendTextAsync" /> sends) is removed from what the pane shows
    /// before matching, including the part of a line the shell has echoed so
    /// far. Waiting for <c>done</c> after typing <c>echo done</c> therefore
    /// waits for the command's output, not for the line that was typed.
    /// </para>
    /// <para>
    /// The removal is by text: output identical to a typed line, as a whole
    /// word, is removed too. Key names, and text sent with
    /// <see cref="SendKeysRequest.ExpandFormats" />,
    /// <see cref="SendKeysRequest.Repeat" /> or a copy-mode command, are not
    /// removed.
    /// </para>
    /// <para>
    /// A typed line the pane wrapped onto a second row is still recognised.
    /// Like <see cref="WaitForTextAsync(PaneWaitRequest, CancellationToken)" />,
    /// the wait sleeps on the pane's output through a control-mode client.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The request has no patterns; the echo alone would answer it.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative or longer than 49 days.</exception>
    /// <exception cref="TmuxPaneException">The pane's program had already exited.</exception>
    /// <exception cref="TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
    /// <exception cref="LibTmuxException">
    /// The text was sent but a requested Enter failed. The pane may already
    /// have acted on the text; do not retry the whole call.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<PaneWaitResult> SendKeysAndWaitAsync(
        SendKeysRequest keys,
        PaneWaitRequest wait,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentOutOfRangeException.ThrowIfLessThan(wait.Timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(wait.Timeout, PaneTextWaiter.LongestTimeout);
        if (wait.Patterns.Count == 0)
        {
            throw new ArgumentException("Waiting for any output would end on the keys' own echo; name a pattern.", nameof(wait));
        }

        (PaneWaitOutcome outcome, string? pattern, TimeSpan elapsed) = await PaneTextWaiter
            .WaitAsync(
                this,
                PaneActivityHub.Shared,
                AfterSending(wait, PlainText(keys)),
                wait.Timeout,
                PaneReader.Failure,
                progress: null,
                cancellationToken,
                token => SendKeysAsync(keys, token))
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
    /// <exception cref="TmuxObjectNotFoundException">tmux no longer has the pane.</exception>
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

    /// <summary>Judges what a pane printed after keys were sent, discounting their echo.</summary>
    /// <param name="wait">The patterns that end the wait.</param>
    /// <param name="typed">The literal text sent, or null when keys were sent by name.</param>
    /// <returns>A classifier that ignores the screen at entry.</returns>
    internal static Func<IReadOnlyList<string>, bool, PaneWaitVerdict?> AfterSending(
        PaneWaitRequest wait,
        string? typed)
    {
        Regex[] wanted = [.. wait.Patterns];
        Regex[] stops = [.. wait.StopPatterns];
        Func<IReadOnlyList<string>, IReadOnlyList<string>> withoutEcho =
            typed is { Length: > 0 } ? PaneText.TypedEchoRemover(typed) : rows => rows;
        return (rows, atEntry) =>
        {
            if (atEntry)
            {
                return null;
            }

            IReadOnlyList<string> lines = withoutEcho(rows);
            if (Match(stops, lines) is { } stopped)
            {
                return new(PaneWaitOutcome.Stopped, stopped);
            }

            return Match(wanted, lines) is { } matched ? new(PaneWaitOutcome.Matched, matched) : null;
        };
    }

    // Only text tmux types exactly as given has a predictable echo.
    private static string? PlainText(SendKeysRequest keys) =>
        keys is { Literal: true, ExpandFormats: false, Repeat: null, CopyModeCommand: null } ? keys.Text : null;

    private static Regex Containing(string text, [CallerArgumentExpression(nameof(text))] string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text, name);
        if (text.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException("Matching is line by line, so the text cannot contain a line break.", name);
        }

        return new Regex(Regex.Escape(text), RegexOptions.CultureInvariant);
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
