using System.Runtime.Versioning;
using LibTmux.Mcp;

namespace LibTmux.UnitTests.Mcp;

/// <summary>
/// Fast, tmux-free coverage of <see cref="PaneEchoRegistry" />'s key model,
/// word-boundary mask, TTL/cap bounds, record/settle/rollback, and
/// server-identity keying. The real-tmux scenarios (S1-S6 from the echo
/// contract) live in <c>PaneEchoContractTests</c>; this is the inner loop for
/// the logic those scenarios exercise end to end. Every test builds its own
/// <see cref="PaneRunRegistry.PaneRunIdentity" /> directly - the registry is
/// keyed by that struct alone, so no real pane, socket, or tmux process is
/// needed to exercise it.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class PaneEchoRegistryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1);

    private static int _paneCounter;

    [Fact]
    public void WithoutEcho_removes_only_whole_word_occurrences()
    {
        Assert.Equal("$ ready", PaneEchoRegistry.WithoutEcho("$ ready", "y"));
        Assert.Equal("$ ", PaneEchoRegistry.WithoutEcho("$ y", "y"));
        Assert.Equal("uid=1000", PaneEchoRegistry.WithoutEcho("uid=1000", "id"));
        Assert.Equal("$ ", PaneEchoRegistry.WithoutEcho("$ id", "id"));
    }

    [Fact]
    public void WithoutEchoes_removes_every_distinct_echo_each_as_a_whole_unit()
    {
        Assert.Equal(
            "\nMARKER",
            PaneEchoRegistry.WithoutEchoes("echo MARKER\nMARKER", ["echo MARKER"]));
    }

    [Fact]
    public void NoteKeyDispatch_tracks_literal_fallback_text_eagerly()
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();

        // Typing only ever adds protection, so it is visible immediately -
        // no confirmation needed.
        PaneEchoRegistry.NoteKeyDispatch(identity, "echo MARKER", enter: false, Now);
        Assert.Equal("echo MARKER", PaneEchoRegistry.GetLiveEcho(identity, Now).Pending);
    }

    [Fact]
    public void A_submit_key_stays_pending_until_its_dispatch_is_settled()
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();
        PaneEchoRegistry.NoteKeyDispatch(identity, "echo MARKER", enter: false, Now);

        // Submitting moves the line from `Pending` (never expires) into
        // `Recent` (expires); an unconfirmed submit must not do that yet, or
        // a pane still genuinely showing the line unsubmitted goes unmasked.
        PaneEchoRegistry.PaneEchoNote note = PaneEchoRegistry.NoteKeyDispatch(
            identity, "Enter", enter: false, Now);
        PaneEchoRegistry.LiveEcho unsettled = PaneEchoRegistry.GetLiveEcho(identity, Now);
        Assert.Equal("echo MARKER", unsettled.Pending);
        Assert.Empty(unsettled.Recent);

        note.Settle(Now);
        PaneEchoRegistry.LiveEcho settled = PaneEchoRegistry.GetLiveEcho(identity, Now);
        Assert.Equal(string.Empty, settled.Pending);
        Assert.Equal(["echo MARKER"], settled.Recent);
    }

    [Fact]
    public void Enter_on_a_literal_write_also_stays_pending_until_settled()
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();

        // send_keys(literal: true, enter: true) sends Enter as a separate
        // tmux command after the text, so this defers settling the same way
        // NoteKeyDispatch does.
        PaneEchoRegistry.PaneEchoNote note = PaneEchoRegistry.NoteLiteralWrite(
            identity, "echo MARKER", enter: true, Now);
        PaneEchoRegistry.LiveEcho unsettled = PaneEchoRegistry.GetLiveEcho(identity, Now);
        Assert.Equal("echo MARKER", unsettled.Pending);
        Assert.Empty(unsettled.Recent);

        note.Settle(Now);
        PaneEchoRegistry.LiveEcho settled = PaneEchoRegistry.GetLiveEcho(identity, Now);
        Assert.Equal(string.Empty, settled.Pending);
        Assert.Equal(["echo MARKER"], settled.Recent);
    }

    [Fact]
    public void BSpace_erases_exactly_including_overflow_into_an_earlier_calls_text()
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();

        PaneEchoRegistry.NoteKeyDispatch(identity, "xMARKER", enter: false, Now);
        for (int index = 0; index < 7; index++)
        {
            // The drop is confirmed each time, matching a real caller: one
            // send_keys call per backspace, settled once its own dispatch
            // succeeds.
            PaneEchoRegistry.NoteKeyDispatch(identity, "BSpace", enter: false, Now).Settle(Now);
        }

        PaneEchoRegistry.LiveEcho midway = PaneEchoRegistry.GetLiveEcho(identity, Now);
        Assert.Equal(string.Empty, midway.Pending);
        // The fully-erased span is still captured: a shell that redraws by
        // `\r` leaves it sitting in the tail as its own line, unaffected by
        // the erase.
        Assert.Contains("xMARKER", midway.Recent);

        // One more BSpace than there is text to erase is a no-op, not an
        // underflow.
        PaneEchoRegistry.NoteKeyDispatch(identity, "BSpace", enter: false, Now).Settle(Now);
        Assert.Equal(string.Empty, PaneEchoRegistry.GetLiveEcho(identity, Now).Pending);
    }

    [Fact]
    public void An_unconfirmed_erase_leaves_the_pre_edit_text_fully_protected()
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();
        PaneEchoRegistry.NoteKeyDispatch(identity, "xMARKER", enter: false, Now);

        // The capture into `recent` is additive and commits eagerly; the
        // character is still there until the erase is confirmed.
        PaneEchoRegistry.NoteKeyDispatch(identity, "BSpace", enter: false, Now);
        PaneEchoRegistry.LiveEcho unsettled = PaneEchoRegistry.GetLiveEcho(identity, Now);
        Assert.Equal("xMARKER", unsettled.Pending);
        Assert.Contains("xMARKER", unsettled.Recent);
    }

    [Theory]
    [InlineData("C-u")]
    [InlineData("C-c")]
    public void Kill_line_keys_discard_the_line_but_still_protect_what_they_discarded(string killKey)
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();

        PaneEchoRegistry.NoteKeyDispatch(identity, "oops", enter: false, Now);
        PaneEchoRegistry.NoteKeyDispatch(identity, killKey, enter: false, Now).Settle(Now);

        PaneEchoRegistry.LiveEcho after = PaneEchoRegistry.GetLiveEcho(identity, Now);
        Assert.Equal(string.Empty, after.Pending);
        Assert.Contains("oops", after.Recent);
    }

    [Fact]
    public void DC_is_a_noop()
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();

        PaneEchoRegistry.NoteKeyDispatch(identity, "MARKER", enter: false, Now);
        PaneEchoRegistry.NoteKeyDispatch(identity, "DC", enter: false, Now).Settle(Now);

        Assert.Equal("MARKER", PaneEchoRegistry.GetLiveEcho(identity, Now).Pending);
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Home")]
    [InlineData("End")]
    [InlineData("Tab")]
    [InlineData("C-a")]
    [InlineData("F5")]
    public void An_unmodelled_key_clears_the_line_rather_than_keeping_it_stale(string unknownKey)
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();

        PaneEchoRegistry.NoteKeyDispatch(identity, "xMARKER", enter: false, Now);
        PaneEchoRegistry.NoteKeyDispatch(identity, unknownKey, enter: false, Now).Settle(Now);

        PaneEchoRegistry.LiveEcho after = PaneEchoRegistry.GetLiveEcho(identity, Now);
        Assert.Equal(string.Empty, after.Pending);
        // Fails open, unlike libtmux-go's reference model: nothing is carried
        // into `recent` either, so a wait stops discounting this line instead
        // of masking output with a capture that may no longer describe it.
        Assert.Empty(after.Recent);
    }

    [Fact]
    public void An_unconfirmed_unknown_key_leaves_the_line_exactly_as_it_was()
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();
        PaneEchoRegistry.NoteKeyDispatch(identity, "xMARKER", enter: false, Now);

        // Left never reached tmux, or we cannot tell: either way, the pane
        // may still be showing "xMARKER" unsubmitted, so clearing the
        // record now would hide it from a wait that reads next.
        PaneEchoRegistry.NoteKeyDispatch(identity, "Left", enter: false, Now);
        Assert.Equal("xMARKER", PaneEchoRegistry.GetLiveEcho(identity, Now).Pending);
    }

    [Fact]
    public void A_submitted_line_remains_discounted_after_enter_clears_pending()
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();

        PaneEchoRegistry.NoteKeyDispatch(identity, "echo MARKER", enter: true, Now).Settle(Now);

        PaneEchoRegistry.LiveEcho after = PaneEchoRegistry.GetLiveEcho(identity, Now);
        Assert.Equal(string.Empty, after.Pending);
        Assert.Equal(["echo MARKER"], after.Recent);
    }

    [Fact]
    public void A_literal_multiline_write_submits_each_embedded_line_as_its_own_recent_entry()
    {
        // Departs from the ts port, which masks one joined byte stream and
        // keeps a multi-line write as one blob. This port masks pane rows, so
        // each embedded line needs its own entry or the second line's row
        // would never match the recorded text.
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();

        PaneEchoRegistry.NoteLiteralWrite(identity, "echo one\necho two", enter: true, Now).Settle(Now);

        PaneEchoRegistry.LiveEcho after = PaneEchoRegistry.GetLiveEcho(identity, Now);
        Assert.Equal(string.Empty, after.Pending);
        Assert.Equal(["echo one", "echo two"], after.Recent);
    }

    [Fact]
    public void An_embedded_newline_settles_the_line_it_completes_eagerly()
    {
        // One literal write is one tmux dispatch even when it embeds a
        // newline, unlike Enter arriving as a separate command - so the line
        // that newline completes is safe to move into `recent` right away,
        // with no confirmation to wait for.
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();

        PaneEchoRegistry.NoteLiteralWrite(identity, "echo one\necho two", enter: false, Now);

        PaneEchoRegistry.LiveEcho after = PaneEchoRegistry.GetLiveEcho(identity, Now);
        Assert.Equal("echo two", after.Pending);
        Assert.Equal(["echo one"], after.Recent);
    }

    [Fact]
    public void Recent_entries_expire_after_the_ttl_but_not_a_moment_before()
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();
        PaneEchoRegistry.NoteKeyDispatch(identity, "echo MARKER", enter: true, Now).Settle(Now);

        DateTimeOffset justBefore = Now + PaneEchoRegistry.RecentTtl - TimeSpan.FromMilliseconds(1);
        Assert.Equal(["echo MARKER"], PaneEchoRegistry.GetLiveEcho(identity, justBefore).Recent);

        DateTimeOffset justAfter = Now + PaneEchoRegistry.RecentTtl + TimeSpan.FromMilliseconds(1);
        Assert.Empty(PaneEchoRegistry.GetLiveEcho(identity, justAfter).Recent);
    }

    [Fact]
    public void Recent_is_bounded_to_the_per_pane_cap()
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();
        for (int index = 0; index < PaneEchoRegistry.RecentCap + 1; index++)
        {
            PaneEchoRegistry.NoteKeyDispatch(identity, $"line{index}", enter: true, Now).Settle(Now);
        }

        IReadOnlyList<string> recent = PaneEchoRegistry.GetLiveEcho(identity, Now).Recent;
        Assert.Equal(PaneEchoRegistry.RecentCap, recent.Count);
        // The oldest is dropped, not the newest.
        Assert.DoesNotContain("line0", recent);
        Assert.Contains($"line{PaneEchoRegistry.RecentCap}", recent);
    }

    [Fact]
    public void A_record_is_keyed_by_server_identity_not_pane_id_alone()
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();
        PaneEchoRegistry.NoteKeyDispatch(identity, "secret", enter: false, Now);
        Assert.Equal("secret", PaneEchoRegistry.GetLiveEcho(identity, Now).Pending);

        PaneRunRegistry.PaneRunIdentity restarted = identity with
        {
            Generation = new ServerGeneration(identity.Generation.ProcessId, identity.Generation.StartTime + 1),
        };
        Assert.Equal(string.Empty, PaneEchoRegistry.GetLiveEcho(restarted, Now).Pending);

        // A write under the new identity does not inherit the old
        // generation's text.
        PaneEchoRegistry.NoteKeyDispatch(restarted, "y", enter: false, Now);
        Assert.Equal("y", PaneEchoRegistry.GetLiveEcho(restarted, Now).Pending);
        Assert.Equal("secret", PaneEchoRegistry.GetLiveEcho(identity, Now).Pending);
    }

    [Fact]
    public void Rollback_restores_the_state_from_before_the_note()
    {
        PaneRunRegistry.PaneRunIdentity identity = FreshIdentity();

        PaneEchoRegistry.PaneEchoNote first = PaneEchoRegistry.NoteLiteralWrite(
            identity, "first", enter: false, Now);
        first.Rollback();
        Assert.Equal(string.Empty, PaneEchoRegistry.GetLiveEcho(identity, Now).Pending);

        PaneEchoRegistry.NoteLiteralWrite(identity, "first", enter: false, Now);
        PaneEchoRegistry.PaneEchoNote second = PaneEchoRegistry.NoteLiteralWrite(
            identity, "second", enter: false, Now);
        Assert.Equal("firstsecond", PaneEchoRegistry.GetLiveEcho(identity, Now).Pending);

        second.Rollback();
        Assert.Equal("first", PaneEchoRegistry.GetLiveEcho(identity, Now).Pending);
    }

    private static PaneRunRegistry.PaneRunIdentity FreshIdentity()
    {
        int pane = Interlocked.Increment(ref _paneCounter);
        return new PaneRunRegistry.PaneRunIdentity(
            new PaneInputEndpointIdentity((ulong)pane, (ulong)pane),
            new ServerGeneration(4242, 1_000_000),
            new PaneId(pane));
    }
}
