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
    /// <exception cref="TmuxTransportException">Control observation failed and polling was not enabled.</exception>
    /// <exception cref="TmuxObjectNotFoundException">tmux no longer has the pane when the wait begins.</exception>
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
    /// The wait sleeps on the pane's output through an owned control-mode client.
    /// It attaches with <c>ignore-size</c>, and fallback to timed reads requires
    /// <see cref="PaneWaitRequest.AllowPollingFallback" />.
    /// </para>
    /// <para>
    /// A pane whose original program has exited ends the wait as
    /// <see cref="PaneWaitOutcome.PaneExited" />, including at entry. A pane
    /// removed during observation also ends the wait as PaneExited.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative or longer than 49 days.</exception>
    /// <exception cref="TmuxTransportException">Control observation failed and polling was not enabled.</exception>
    /// <exception cref="TmuxObjectNotFoundException">tmux no longer has the pane when the wait begins.</exception>
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
        ValidatedPaneWaitRequest options = request.Snapshot();
        return await PaneTextWaiter.WaitAsync(
            PaneActivityHub.Shared, this, options, matchLines: null, tailLines: null,
            progress: progress is null ? null : (elapsed, _, message) => progress(elapsed, message),
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
    /// <exception cref="TmuxPaneException">The pane's program had already exited, or the pane changed during every read until the timeout, so nothing was sent.</exception>
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
    /// <exception cref="TmuxPaneException">The pane's program had already exited, or the pane changed during every read until the timeout, so nothing was sent.</exception>
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
        ValidatedPaneWaitRequest options = wait.Snapshot();
        if (options.Wanted.Length == 0)
        {
            throw new ArgumentException("Waiting for any output would end on the keys' own echo; name a pattern.", nameof(wait));
        }

        string? typed = PlainText(keys);
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? withoutEcho =
            typed is { Length: > 0 } ? PaneText.TypedEchoRemover(typed) : null;
        return await PaneTextWaiter.WaitAsync(
            PaneActivityHub.Shared, this, options, withoutEcho, withoutEcho,
            progress: null, cancellationToken: cancellationToken,
            afterEntry: token => SendKeysAsync(keys, token)).ConfigureAwait(false);
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
    /// <exception cref="TmuxTransportException">Control observation failed.</exception>
    /// <exception cref="TmuxObjectNotFoundException">tmux no longer has the pane when the wait begins.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<PaneWaitResult> WaitUntilAsync(
        Func<IReadOnlyList<string>, bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return await PaneTextWaiter.WaitForScreenAsync(
            this, PaneActivityHub.Shared, condition, timeout, cancellationToken)
            .ConfigureAwait(false);
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
}
