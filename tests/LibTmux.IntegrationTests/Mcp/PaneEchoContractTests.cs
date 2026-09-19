using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Mcp;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests;

/// <summary>
/// The cross-port echo contract: <c>wait_for_text</c> must never treat this
/// server's own typing as pane output, and must never let masking that typing
/// hide real output either.
/// </summary>
/// <remarks>
/// Scenarios S1-S4 and S6 from the contract, run against real tmux. S5 (a cold
/// shell that has not drawn its first prompt) is covered too, alongside a
/// control that shows the same guard still excludes an unconfirmed echo.
/// The classifier, word-boundary mask, TTL, cap, and identity keying these
/// scenarios exercise end to end have their own fast, tmux-free coverage in
/// <c>PaneEchoRegistryTests</c>. This design tracks typed text by content, not
/// by cursor row or position, so it has nothing for a window resize to
/// invalidate; no separate resize control is needed.
/// </remarks>
[Collection("tmux control clients")]
[UnsupportedOSPlatform("windows")]
public sealed class PaneEchoContractTests
{
    [UnixFact]
    public async Task S1_a_short_unsubmitted_answer_does_not_mask_a_longer_real_output_line()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        Pane pane = await BashPaneAsync(scope, token);
        string paneId = pane.Id.ToString();
        string tty = await PaneTtyAsync(pane, token);

        // A short answer that is also the last letter of the real output
        // "ready" below - typed but never submitted.
        await mcp.Write.SendKeysAsync(
            "y", paneId, enter: false, literal: true, cancellationToken: token);

        Task<WaitResult> waiting = mcp.Read.WaitForTextAsync(
            paneId, ["ready"], timeoutSeconds: 3, cancellationToken: token);
        await InjectForeignOutputAsync(scope.Server, tty, "ready", 0.3, token);
        WaitResult waited = await waiting;

        Assert.Equal(WaitOutcome.Matched, waited.Outcome);
        Assert.Equal("ready", waited.MatchedPattern);
    }

    [UnixFact]
    public async Task S2_a_submitted_commands_echo_is_not_the_match_wait_started_before_the_send()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        Pane pane = await BashPaneAsync(scope, token);
        string paneId = pane.Id.ToString();

        var stopwatch = Stopwatch.StartNew();
        Task<WaitResult> waiting = mcp.Read.WaitForTextAsync(
            paneId, ["MARKER"], timeoutSeconds: 4, cancellationToken: token);
        await mcp.Write.SendKeysAsync(
            "sleep 1; echo MARKER", paneId, enter: true, literal: true, cancellationToken: token);

        WaitResult waited = await waiting;

        Assert.Equal(WaitOutcome.Matched, waited.Outcome);
        Assert.Equal("MARKER", waited.MatchedPattern);
        // The wait already saw the command's own echo in its stream; an
        // instant match on that buffered echo could not have taken as long as
        // the pane's own `sleep 1`, so timing is what distinguishes a match on
        // the echo from a match on the real output row beneath it.
        Assert.True(
            stopwatch.Elapsed.TotalSeconds >= 0.7,
            $"expected the wait to take at least 700ms, took {stopwatch.Elapsed.TotalSeconds}s");
    }

    [UnixFact]
    public async Task S2_a_submitted_commands_echo_is_not_the_match_wait_started_after_the_send()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        Pane pane = await BashPaneAsync(scope, token);
        string paneId = pane.Id.ToString();

        await mcp.Write.SendKeysAsync(
            "sleep 1; echo MARKER", paneId, enter: true, literal: true, cancellationToken: token);
        var stopwatch = Stopwatch.StartNew();
        WaitResult waited = await mcp.Read.WaitForTextAsync(
            paneId, ["MARKER"], timeoutSeconds: 4, cancellationToken: token);

        Assert.Equal(WaitOutcome.Matched, waited.Outcome);
        Assert.Equal("MARKER", waited.MatchedPattern);
        // Here the echo printed before the wait ever subscribed, so it can
        // never be what matched - only the pane's own `sleep 1` gates the
        // real output, and the same timing check proves it was not a replay
        // of something already on screen either.
        Assert.True(
            stopwatch.Elapsed.TotalSeconds >= 0.7,
            $"expected the wait to take at least 700ms, took {stopwatch.Elapsed.TotalSeconds}s");
    }

    [UnixFact]
    public async Task S3_unsubmitted_text_times_out_rather_than_matching_its_own_echo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        Pane pane = await BashPaneAsync(scope, token);
        string paneId = pane.Id.ToString();

        Task<WaitResult> waiting = mcp.Read.WaitForTextAsync(
            paneId, ["MARKER"], timeoutSeconds: 1, cancellationToken: token);
        await mcp.Write.SendKeysAsync(
            "echo MARKER", paneId, enter: false, literal: true, cancellationToken: token);

        WaitResult waited = await waiting;

        Assert.Equal(WaitOutcome.Timeout, waited.Outcome);
        Assert.Null(waited.MatchedPattern);
    }

    [UnixFact]
    public async Task S4_edits_are_applied_and_key_names_never_join_the_tracked_text()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        Pane pane = await BashPaneAsync(scope, token);
        string paneId = pane.Id.ToString();

        Task<WaitResult> waiting = mcp.Read.WaitForTextAsync(
            paneId, ["MARKER"], timeoutSeconds: 4, cancellationToken: token);

        await mcp.Write.SendKeysAsync(
            "xMARKER", paneId, enter: false, literal: true, cancellationToken: token);
        for (int index = 0; index < 7; index++)
        {
            await mcp.Write.SendKeysAsync(
                "BSpace", paneId, enter: false, literal: false, cancellationToken: token);
        }

        var stopwatch = Stopwatch.StartNew();
        // `sleep 1` beyond the contract's own "echo MARKER" is a test-side
        // discriminator only: without it, a false match on the submitted
        // line's own echo and a correct match on the real output row would
        // arrive at the same instant and nothing here could tell them apart.
        await mcp.Write.SendKeysAsync(
            "sleep 1; echo MARKER", paneId, enter: true, literal: true, cancellationToken: token);

        WaitResult waited = await waiting;

        Assert.Equal(WaitOutcome.Matched, waited.Outcome);
        Assert.Equal("MARKER", waited.MatchedPattern);
        Assert.True(
            stopwatch.Elapsed.TotalSeconds >= 0.7,
            $"expected the wait to take at least 700ms, took {stopwatch.Elapsed.TotalSeconds}s");
    }

    [UnixFact]
    public async Task S5_typing_into_a_cold_shell_never_matches_its_own_type_ahead()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        // `-echo` disables the pty's own echo, so nothing is drawn until
        // `exec cat` re-prints whatever was queued as type-ahead. A fixed
        // sleep before `stty` takes effect would race it: on a loaded
        // machine the marker could land while the pty is still kernel-echoed.
        // `wait-for` makes "stty already ran" an observed fact instead.
        string channel = $"qa-cold-shell-{Guid.NewGuid():N}";
        Window cold = await scope.Session.CreateWindowAsync(
            new NewWindowRequest
            {
                Command = $"stty raw -echo; tmux wait-for -S {channel}; sleep 0.3; exec cat",
            },
            token);
        string paneId = (await cold.GetPanesAsync(token))[0].Id.ToString();
        _ = await mcp.Write.WaitForChannelAsync(channel, timeoutSeconds: 5, cancellationToken: token);

        string marker = $"QAMARK{Guid.NewGuid():N}"[..16];
        await mcp.Write.SendKeysAsync(
            marker, paneId, enter: false, literal: true, cancellationToken: token);
        WaitResult waited = await mcp.Read.WaitForTextAsync(
            paneId, [marker], timeoutSeconds: 1.2, cancellationToken: token);

        Assert.Equal(WaitOutcome.Timeout, waited.Outcome);
        Assert.Null(waited.MatchedPattern);
    }

    [UnixFact]
    public async Task S6_an_unmodelled_key_stops_discounting_the_panes_current_line()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        Pane pane = await BashPaneAsync(scope, token);
        string paneId = pane.Id.ToString();
        string tty = await PaneTtyAsync(pane, token);

        await mcp.Write.SendKeysAsync(
            "xMARKER", paneId, enter: false, literal: true, cancellationToken: token);
        // Left is a key send_keys cannot apply to the tracked line at all.
        await mcp.Write.SendKeysAsync(
            "Left", paneId, enter: false, literal: false, cancellationToken: token);

        Task<WaitResult> waiting = mcp.Read.WaitForTextAsync(
            paneId, ["xMARKER"], timeoutSeconds: 3, cancellationToken: token);
        await InjectForeignOutputAsync(scope.Server, tty, "xMARKER", 0.3, token);
        WaitResult waited = await waiting;

        // The abandoned typing must not still be masking a later, genuine
        // line that happens to repeat it. Either Matched or PresentAtEntry
        // proves the guard stopped hiding it; the contract tolerates
        // reporting the echo itself as output here.
        Assert.True(
            waited.Outcome is WaitOutcome.Matched or WaitOutcome.PresentAtEntry,
            $"expected Matched or PresentAtEntry, got {waited.Outcome}");
        Assert.Equal("xMARKER", waited.MatchedPattern);
    }

    private static async Task<Pane> BashPaneAsync(TemporaryHierarchyScope scope, CancellationToken token)
    {
        // A readline redraw and a real prompt need a real line editor, which
        // the scope's default shell is not guaranteed to have.
        Window shell = await scope.Session.CreateWindowAsync(
            new NewWindowRequest { Command = "bash --norc --noprofile -i" },
            token);
        return (await shell.GetPanesAsync(token))[0];
    }

    private static async Task<string> PaneTtyAsync(Pane pane, CancellationToken token)
    {
        IReadOnlyList<string>? lines = await pane.DisplayMessageAsync(
                new DisplayMessageRequest { Message = "#{pane_tty}", ReturnText = true },
                token)
            .ConfigureAwait(false);
        string? tty = lines is { Count: > 0 } ? lines[0] : null;
        if (string.IsNullOrEmpty(tty))
        {
            throw new InvalidOperationException($"pane {pane.Id} reported no tty");
        }

        return tty;
    }

    /// <summary>Writes <paramref name="line"/> directly onto a pane's tty, after a delay.</summary>
    /// <remarks>
    /// Runs on the tmux server itself via <c>run-shell -b</c>, so the write is
    /// never dispatched through this MCP server and never recorded as
    /// something it typed.
    /// </remarks>
    private static Task<TmuxCommandResult> InjectForeignOutputAsync(
        Server server,
        string tty,
        string line,
        double delaySeconds,
        CancellationToken token) =>
        server.ExecuteCommandAsync(
            [
                "run-shell",
                "-b",
                $"sleep {delaySeconds.ToString(CultureInfo.InvariantCulture)}; "
                    + $"printf '%s\\n' {WriteTools.ShellQuote(line)} > {WriteTools.ShellQuote(tty)}",
            ],
            token);
}
