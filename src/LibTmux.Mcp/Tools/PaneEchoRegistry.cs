using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <summary>
/// What this server has typed into a pane that a wait must not mistake for
/// the pane's own output.
/// </summary>
/// <remarks>
/// <para>
/// A pane echoes what is typed into it. A wait watching for text a caller
/// just sent - <c>send_keys(pane, "echo MARKER", enter: true)</c> then a wait
/// for <c>MARKER</c> - would otherwise be answered by that echo, before the
/// command has done anything. Nothing about <em>when</em> the echo arrives
/// sets it apart from real output, so it is told apart by <em>what it is</em>:
/// the exact text this process itself dispatched, tracked here key by key and
/// discounted while a wait decides whether something matched.
/// </para>
/// <para>
/// Two pieces, matching the two ways typed text stops being "just typed":
/// <see cref="PaneEchoState.Pending" /> is the line still being edited - built
/// key by key, corrected by a backspace, abandoned by a kill, all before
/// anything is submitted. <see cref="PaneEchoState.Recent" /> is what a line
/// became once it ended (submitted or killed), kept for a bounded time so a
/// wait still reading the same buffered bytes does not stop discounting a
/// line's echo just because wall-clock time has passed since it was sent.
/// </para>
/// <para>
/// Keyed by <see cref="PaneRunRegistry.PaneRunIdentity" />, which already
/// combines the tmux socket, the server's pid and start time, and the pane
/// id: a pane id tmux hands out again after a server restart carries no
/// record forward.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static partial class PaneEchoRegistry
{
    /// <summary>
    /// Long enough to cover ordinary latency between submitting a line and a
    /// wait still reading its buffered echo; short enough that a pane reused
    /// for something else is not stuck discounting an old line indefinitely.
    /// </summary>
    internal static readonly TimeSpan RecentTtl = TimeSpan.FromSeconds(10);

    /// <summary>A command and the keys that submit it, with room to spare; the oldest is dropped.</summary>
    internal const int RecentCap = 4;

    private static readonly ConcurrentDictionary<PaneRunRegistry.PaneRunIdentity, PaneEchoState> ByPane = [];

    private static readonly FrozenSet<string> SubmitKeys =
        new HashSet<string>(["C-m", "Enter", "KPEnter"], StringComparer.Ordinal).ToFrozenSet();

    private static readonly FrozenSet<string> KillLineKeys =
        new HashSet<string>(["C-c", "C-u"], StringComparer.Ordinal).ToFrozenSet();

    private static readonly FrozenSet<string> EraseKeys =
        new HashSet<string>(["BSpace", "C-h"], StringComparer.Ordinal).ToFrozenSet();

    private static readonly FrozenSet<string> NoopKeys =
        new HashSet<string>(["DC"], StringComparer.Ordinal).ToFrozenSet();

    /// <summary>A deferred change to a pane's echo state, applied only once its dispatch is confirmed.</summary>
    internal delegate PaneEchoState SettleAction(PaneEchoState state, DateTimeOffset now);

    /// <summary>Records one non-literal key-name dispatch, before it reaches tmux.</summary>
    /// <param name="pane">The pane the key is being sent to.</param>
    /// <param name="keys">The key name, or literal fallback text send_keys would type.</param>
    /// <param name="enter">Whether Enter follows, as part of the same dispatch or a later one.</param>
    /// <returns>
    /// A token covering this note. Call <see cref="PaneEchoNote.Rollback" /> only when the
    /// dispatch is known not to have reached tmux at all, and
    /// <see cref="PaneEchoNote.Settle()" /> once it is confirmed to have reached tmux; an
    /// ambiguous outcome should call neither, since the text may already be
    /// sitting on the pane and whether its line ended is not yet known.
    /// </returns>
    internal static PaneEchoNote NoteKeyDispatch(Pane pane, string keys, bool enter)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TryIdentify(pane) is { } identity
            ? NoteKeyDispatch(identity, keys, enter, DateTimeOffset.UtcNow)
            : PaneEchoNote.NoOp;
    }

    /// <summary>Records one literal write - <c>send_keys(literal: true)</c> or a paste - before it reaches tmux.</summary>
    /// <param name="pane">The pane the text is being sent to.</param>
    /// <param name="text">The literal text tmux will type.</param>
    /// <param name="enter">Whether Enter (a trailing newline) follows, in the same dispatch or a later one.</param>
    /// <returns>A token covering this note; see <see cref="NoteKeyDispatch(Pane, string, bool)" />.</returns>
    internal static PaneEchoNote NoteLiteralWrite(Pane pane, string text, bool enter)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TryIdentify(pane) is { } identity
            ? NoteLiteralWrite(identity, text, enter, DateTimeOffset.UtcNow)
            : PaneEchoNote.NoOp;
    }

    /// <summary>What this server has typed into <paramref name="pane" /> that a wait should discount right now.</summary>
    internal static LiveEcho GetLiveEcho(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TryIdentify(pane) is { } identity
            ? GetLiveEcho(identity, DateTimeOffset.UtcNow)
            : LiveEcho.None;
    }

    internal static PaneEchoNote NoteKeyDispatch(
        PaneRunRegistry.PaneRunIdentity identity, string keys, bool enter, DateTimeOffset now)
    {
        PaneEchoState before = ByPane.TryGetValue(identity, out PaneEchoState? existing)
            ? existing
            : PaneEchoState.Empty;
        (PaneEchoState eager, SettleAction? settle) = PlanKeyEffect(before, ClassifyKey(keys), now);
        ByPane[identity] = eager;
        return new PaneEchoNote(identity, before, enter ? Combine(settle, EndLine) : settle);
    }

    internal static PaneEchoNote NoteLiteralWrite(
        PaneRunRegistry.PaneRunIdentity identity, string text, bool enter, DateTimeOffset now)
    {
        PaneEchoState before = ByPane.TryGetValue(identity, out PaneEchoState? existing)
            ? existing
            : PaneEchoState.Empty;
        ByPane[identity] = AppendText(before, text, now);
        return new PaneEchoNote(identity, before, enter ? EndLine : null);
    }

    /// <summary>
    /// What this server has typed into the pane named by <paramref name="identity" />
    /// that a wait should discount right now.
    /// </summary>
    /// <remarks>
    /// Returns nothing for a record left by a different server generation: a
    /// pane id tmux hands out again after a restart carries no memory forward.
    /// </remarks>
    internal static LiveEcho GetLiveEcho(PaneRunRegistry.PaneRunIdentity identity, DateTimeOffset now)
    {
        if (!ByPane.TryGetValue(identity, out PaneEchoState? state))
        {
            return LiveEcho.None;
        }

        return new LiveEcho(state.Pending, [.. PruneRecent(state.Recent, now).Select(entry => entry.Text)]);
    }

    private static SettleAction? Combine(SettleAction? first, SettleAction? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return (state, now) => second(first(state, now), now);
    }

    /// <summary>
    /// Splits what one key does into the part safe to commit immediately and
    /// the part that must wait for confirmation.
    /// </summary>
    /// <remarks>
    /// Submitting, killing, or abandoning a line all remove protection
    /// (moving text out of <see cref="PaneEchoState.Pending" />, which never
    /// expires, or clearing it outright), so a wait already watching must
    /// never see that removal before the key is confirmed to have actually
    /// reached tmux - recording it eagerly reopened exactly the case this
    /// exists to prevent, when the key that ends a line travels as a
    /// separate tmux command from the text before it. Typing a character
    /// only adds protection, so it commits right away.
    /// </remarks>
    private static (PaneEchoState Eager, SettleAction? Settle) PlanKeyEffect(
        PaneEchoState state, KeyEffect effect, DateTimeOffset now) =>
        effect.Kind switch
        {
            KeyEffectKind.Submit or KeyEffectKind.Kill => (state, EndLine),
            KeyEffectKind.Erase => PlanErase(state, now),
            KeyEffectKind.Noop => (state, null),
            // Fails open: nothing about this line is carried into `recent`,
            // so a wait stops discounting it rather than keeping a capture
            // that may no longer describe what is really on the pane's line.
            KeyEffectKind.Unknown => (state, static (s, _) => s with { Pending = string.Empty, PendingCaptured = false }),
            KeyEffectKind.Text => (state with { Pending = state.Pending + effect.Text, PendingCaptured = false }, null),
            _ => (state, null),
        };

    private static (PaneEchoState Eager, SettleAction? Settle) PlanErase(PaneEchoState state, DateTimeOffset now)
    {
        // The pre-erase value is the longest this edit has reached; capture
        // it once, before shrinking, so a shell's redraw-by-`\r` echo of it
        // stays discounted even after `Pending` no longer says it.
        PaneEchoState eager = state.PendingCaptured
            ? state
            : PushRecentLines(state, state.Pending, now) with { PendingCaptured = true };
        return (eager, static (s, _) => s with { Pending = DropLastRune(s.Pending) });
    }

    /// <summary>Appends literal text to a pane's pending line, settling any complete line it embeds.</summary>
    /// <remarks>
    /// One literal write is one tmux dispatch, whether or not it embeds a
    /// newline, so - unlike Enter arriving as a separate command - there is
    /// no unconfirmed middle state to protect here: every embedded line
    /// this text completes is safe to move into `recent` immediately. Only
    /// the trailing, still-open segment stays in `Pending`.
    /// </remarks>
    private static PaneEchoState AppendText(PaneEchoState state, string text, DateTimeOffset now)
    {
        if (text.Length == 0)
        {
            return state;
        }

        string[] segments = text.Split(['\r', '\n']);
        if (segments.Length == 1)
        {
            return state with { Pending = state.Pending + text, PendingCaptured = false };
        }

        // An embedded newline submits everything up to it the moment tmux
        // takes it; nothing distinguishes an earlier line inside the same
        // write from one sent on its own.
        state = PushRecentLines(state, state.Pending + segments[0], now);
        for (int index = 1; index < segments.Length - 1; index++)
        {
            state = PushRecent(state, segments[index], now);
        }

        return state with { Pending = segments[^1], PendingCaptured = false };
    }

    /// <summary>A line ends: whatever was pending moves to `recent`, and pending resets.</summary>
    private static PaneEchoState EndLine(PaneEchoState state, DateTimeOffset now)
    {
        state = PushRecentLines(state, state.Pending, now);
        return state with { Pending = string.Empty, PendingCaptured = false };
    }

    /// <summary>
    /// Pushes <paramref name="text" /> into `recent`, one row at a time.
    /// </summary>
    /// <remarks>
    /// A wait masks pane rows, not a joined byte stream, and no row contains
    /// an embedded newline - so a multi-line literal write (or a pre-edit
    /// value that somehow spans lines) is split here rather than kept as one
    /// blob, or a later line's row would never match the recorded text.
    /// </remarks>
    private static PaneEchoState PushRecentLines(PaneEchoState state, string text, DateTimeOffset now)
    {
        if (text.Length == 0)
        {
            return state;
        }

        foreach (string line in text.Split(['\r', '\n']))
        {
            state = PushRecent(state, line, now);
        }

        return state;
    }

    private static PaneEchoState PushRecent(PaneEchoState state, string text, DateTimeOffset now)
    {
        if (text.Length == 0)
        {
            return state;
        }

        ImmutableList<RecentEcho> pruned = PruneRecent(state.Recent, now).Add(new RecentEcho(now, text));
        if (pruned.Count > RecentCap)
        {
            pruned = pruned.RemoveRange(0, pruned.Count - RecentCap);
        }

        return state with { Recent = pruned };
    }

    private static ImmutableList<RecentEcho> PruneRecent(ImmutableList<RecentEcho> recent, DateTimeOffset now) =>
        recent.Count == 0 ? recent : recent.RemoveAll(entry => now - entry.At > RecentTtl);

    private static string DropLastRune(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        List<Rune> runes = [.. text.EnumerateRunes()];
        runes.RemoveAt(runes.Count - 1);
        StringBuilder builder = new(text.Length);
        foreach (Rune rune in runes)
        {
            builder.Append(rune);
        }

        return builder.ToString();
    }

    private static PaneRunRegistry.PaneRunIdentity? TryIdentify(Pane pane)
    {
        try
        {
            return PaneRunRegistry.PaneRunIdentity.For(pane);
        }
        catch (McpException)
        {
            return null;
        }
    }

    // -- key classification ---------------------------------------------
    //
    // The reference model is libtmux-go's `pending_input.go`: submit,
    // kill-line, erase and a forward-delete no-op. Diverges from it in one
    // place, called out where it happens.

    /// <summary>
    /// Key names tmux recognizes that this classifier does not model at all -
    /// arrow and navigation keys, function keys, and any other
    /// <c>C-</c>/<c>M-</c>/<c>S-</c> combination beyond the ones above.
    /// </summary>
    /// <remarks>
    /// libtmux-go's model leaves a pane's tracked line untouched here. This
    /// port clears it instead: a key that moves the cursor or edits some
    /// other way means the tracked text no longer reliably describes what is
    /// on the line, and continuing to mask it risks hiding real output that
    /// later happens to repeat that stale text - worse than simply
    /// forgetting it.
    /// </remarks>
    [GeneratedRegex(
        "^(?:BTab|Down|End|Escape|F(?:[1-9]|1[0-9]|2[0-4])|Home|IC|Left|NPage|PPage|"
            + "PageDown|PageUp|Right|Tab|Up)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnhandledKeyNamePattern();

    /// <summary>
    /// Deliberately unanchored at the end: any <c>C-</c>/<c>M-</c>/<c>S-</c>
    /// combination (nested, such as <c>C-M-</c>) is unknown regardless of
    /// what follows the one character after the last dash, which is what
    /// catches a combined key such as <c>C-Left</c> without naming it.
    /// </summary>
    [GeneratedRegex("^(?:C|M|S)(?:-(?:C|M|S))*-.", RegexOptions.CultureInvariant)]
    private static partial Regex ControlOrMetaComboPattern();

    /// <summary>
    /// What one non-literal <c>send_keys</c> token does to the line being
    /// built.
    /// </summary>
    /// <remarks>
    /// A token that is not one of the named keys below is exactly what tmux
    /// itself does with it when it does not match its key table: literal
    /// text, sent character by character. Modelling that fallback, rather
    /// than requiring every token to be a single character, is the one place
    /// this necessarily diverges from a per-key model - <c>send_keys</c>
    /// takes one string, not an array of individual keys, so an ordinary word
    /// or line arrives as a single non-literal token.
    /// </remarks>
    private static KeyEffect ClassifyKey(string key)
    {
        if (SubmitKeys.Contains(key))
        {
            return KeyEffect.Of(KeyEffectKind.Submit);
        }

        if (KillLineKeys.Contains(key))
        {
            return KeyEffect.Of(KeyEffectKind.Kill);
        }

        if (EraseKeys.Contains(key))
        {
            return KeyEffect.Of(KeyEffectKind.Erase);
        }

        if (NoopKeys.Contains(key))
        {
            return KeyEffect.Of(KeyEffectKind.Noop);
        }

        if (key == "Space")
        {
            return KeyEffect.OfText(" ");
        }

        if (key.EnumerateRunes().Count() == 1)
        {
            return KeyEffect.OfText(key);
        }

        if (UnhandledKeyNamePattern().IsMatch(key) || ControlOrMetaComboPattern().IsMatch(key))
        {
            return KeyEffect.Of(KeyEffectKind.Unknown);
        }

        return KeyEffect.OfText(key);
    }

    private enum KeyEffectKind
    {
        Erase,
        Kill,
        Noop,
        Submit,
        Text,
        Unknown,
    }

    private readonly record struct KeyEffect(KeyEffectKind Kind, string? Text = null)
    {
        internal static KeyEffect Of(KeyEffectKind kind) => new(kind);

        internal static KeyEffect OfText(string text) => new(KeyEffectKind.Text, text);
    }

    internal sealed record PaneEchoState(string Pending, bool PendingCaptured, ImmutableList<RecentEcho> Recent)
    {
        internal static readonly PaneEchoState Empty = new(string.Empty, false, []);
    }

    internal readonly record struct RecentEcho(DateTimeOffset At, string Text);

    /// <summary>What this server has typed into a pane that a wait should discount right now.</summary>
    /// <param name="Pending">The line still being edited, or <c>""</c> if there is none.</param>
    /// <param name="Recent">Lines this pane finished within <see cref="RecentTtl" />, oldest first.</param>
    internal readonly record struct LiveEcho(string Pending, IReadOnlyList<string> Recent)
    {
        internal static readonly LiveEcho None = new(string.Empty, []);
    }

    /// <summary>
    /// Covers one <c>Note*</c> call: undo it if its dispatch never reached
    /// tmux, or confirm whatever it deferred once its dispatch is known to
    /// have reached tmux. Doing neither - the ambiguous case - is itself the
    /// correct answer: the record stays exactly as <c>Note*</c> left it,
    /// still fully protecting a line that may or may not have been submitted.
    /// </summary>
    /// <remarks>
    /// Pane input is already serialized by <see cref="PaneRunRegistry" />'s
    /// reservations, so two dispatches to the same pane never race here; a
    /// blind restore or a blind settle is safe because nothing else can have
    /// mutated the record in between.
    /// </remarks>
    internal readonly struct PaneEchoNote
    {
        private readonly PaneRunRegistry.PaneRunIdentity? _identity;
        private readonly PaneEchoState? _before;
        private readonly SettleAction? _settle;

        internal PaneEchoNote(
            PaneRunRegistry.PaneRunIdentity identity,
            PaneEchoState? before,
            SettleAction? settle)
        {
            _identity = identity;
            _before = before;
            _settle = settle;
        }

        /// <summary>Gets a note that does nothing - for a pane whose identity could not be read.</summary>
        internal static PaneEchoNote NoOp => default;

        /// <summary>Restores the record to what it was immediately before the note that returned this.</summary>
        /// <remarks>Call only when the dispatch is known to have never reached tmux at all.</remarks>
        internal void Rollback()
        {
            if (_identity is not { } identity)
            {
                return;
            }

            if (_before is null)
            {
                _ = ByPane.TryRemove(identity, out _);
            }
            else
            {
                ByPane[identity] = _before;
            }
        }

        /// <summary>Applies whatever this note deferred - a submit, a kill, an erase, or an unmodelled key's clear.</summary>
        /// <remarks>Call only once the dispatch is confirmed to have reached tmux.</remarks>
        internal void Settle() => Settle(DateTimeOffset.UtcNow);

        internal void Settle(DateTimeOffset now)
        {
            if (_identity is not { } identity || _settle is null)
            {
                return;
            }

            if (ByPane.TryGetValue(identity, out PaneEchoState? current))
            {
                ByPane[identity] = _settle(current, now);
            }
        }
    }

    // -- whole-occurrence removal -----------------------------------------

    private static bool IsWordChar(char character) => char.IsLetterOrDigit(character) || character == '_';

    /// <summary>Removes every standalone occurrence of <paramref name="echo" /> from <paramref name="text" />.</summary>
    /// <remarks>
    /// An occurrence counts only where it is not part of a longer run of word
    /// characters on either side - what tells a short typed answer apart from
    /// a longer word that merely contains it: <c>y</c> comes off <c>$ y</c>
    /// and stays inside <c>ready</c>. This is what lets a whole recorded line
    /// (<c>echo MARKER</c>) be removed as the exact thing that was typed,
    /// without also erasing an unrelated later line whose real output happens
    /// to repeat one of its words.
    /// </remarks>
    internal static string WithoutEcho(string text, string echo)
    {
        if (echo.Length == 0 || text.Length == 0)
        {
            return text;
        }

        StringBuilder result = new(text.Length);
        int cursor = 0;
        int from = 0;
        while (true)
        {
            int at = text.IndexOf(echo, from, StringComparison.Ordinal);
            if (at < 0)
            {
                break;
            }

            int end = at + echo.Length;
            bool opens = at == 0 || !IsWordChar(text[at - 1]) || !IsWordChar(echo[0]);
            bool closes = end == text.Length || !IsWordChar(text[end]) || !IsWordChar(echo[^1]);
            if (opens && closes)
            {
                result.Append(text, cursor, at - cursor);
                cursor = end;
                from = end;
            }
            else
            {
                from = at + 1;
            }
        }

        result.Append(text, cursor, text.Length - cursor);
        return result.ToString();
    }

    /// <summary><see cref="WithoutEcho" />, applied for every text in <paramref name="echoes" />.</summary>
    internal static string WithoutEchoes(string text, IEnumerable<string> echoes)
    {
        string result = text;
        foreach (string echo in echoes.Where(echo => echo.Length > 0))
        {
            result = WithoutEcho(result, echo);
        }

        return result;
    }
}
