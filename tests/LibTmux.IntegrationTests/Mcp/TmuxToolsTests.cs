using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Mcp;
using LibTmux.Testing;
using ModelContextProtocol;

namespace LibTmux.IntegrationTests;

/// <summary>What the tools do to a real tmux server.</summary>
[Collection("tmux control clients")]
[UnsupportedOSPlatform("windows")]
public sealed class TmuxToolsTests
{
    [UnixFact]
    public async Task Reading_tools_describe_what_tmux_holds()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        HierarchyView view = await mcp.Read.HierarchyAsync(cancellationToken: token);
        Assert.Contains(view.Sessions, session => session.Name == scope.Session.Name);
        Assert.Contains(view.Panes, pane => pane.PaneId == scope.Pane.Id.ToString());

        // Every entity names its parent, which is what lets a caller filter a
        // flat list instead of walking a tree.
        PaneInfo described = view.Panes.Single(pane => pane.PaneId == scope.Pane.Id.ToString());
        Assert.Equal(scope.Window.Id.ToString(), described.WindowId);
        Assert.Equal(scope.Session.Id.ToString(), described.SessionId);
        Assert.False(string.IsNullOrEmpty(described.CurrentCommand));
    }

    [UnixFact]
    public async Task A_pane_that_never_existed_is_refused_by_name()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        McpException missing = await Assert.ThrowsAsync<McpException>(
            () => mcp.Read.CapturePaneAsync("%999", cancellationToken: token));
        Assert.Contains("%999", missing.Message, StringComparison.Ordinal);

        // The message has to say what to do next, or a model retries the same
        // call until it runs out of turn.
        Assert.Contains("tmux_list_panes", missing.Message, StringComparison.Ordinal);

        McpException malformed = await Assert.ThrowsAsync<McpException>(
            () => mcp.Read.CapturePaneAsync("not-a-pane", cancellationToken: token));
        Assert.Contains("%1", malformed.Message, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task Running_a_command_reports_its_real_exit_status()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        RunResult ok = await mcp.Write.RunAsync(
            "echo mcp-ran",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);
        Assert.Equal(0, ok.ExitStatus);
        Assert.False(ok.TimedOut);
        Assert.Contains(ok.Output.Lines, line => line.Contains("mcp-ran", StringComparison.Ordinal));

        // The status comes from the shell rather than from reading the screen,
        // so a command that prints nothing still reports what it did.
        RunResult failed = await mcp.Write.RunAsync(
            "exit 42",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);
        Assert.Equal(42, failed.ExitStatus);
    }

    [UnixFact]
    public async Task A_run_leaves_none_of_its_own_bookkeeping_on_screen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        await mcp.Write.RunAsync("echo first", pane, timeoutSeconds: 20, cancellationToken: token);
        RunResult second = await mcp.Write.RunAsync(
            "echo second",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);

        // The shell echoes the rendezvous like anything else typed, and it
        // stays on screen. A later read must not show an earlier run's, or a
        // reader concludes the command printed tmux commands it never ran.
        Assert.DoesNotContain(second.Output.Lines, line => line.Contains("lt_r_", StringComparison.Ordinal));

        CaptureResult captured = await mcp.Read.CapturePaneAsync(
            pane,
            includeHistory: true,
            cancellationToken: token);
        Assert.DoesNotContain(captured.Content.Lines, line => line.Contains("lt_r_", StringComparison.Ordinal));
        Assert.DoesNotContain(captured.Content.Lines, line => line.Contains("@lt_s_", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task A_wide_prompt_does_not_swallow_what_the_command_printed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        // A prompt this wide leaves the run's own bookkeeping wrapping across
        // rows, which is the shape that made an earlier scrubber read one long
        // joined line as continued and take the next line with it. A macOS
        // runner reaches it without being asked: its hostname is 61 characters.
        await mcp.Write.SendKeysAsync(
            "PS1=$(printf 'x%.0s' $(seq 70))",
            pane,
            enter: true,
            cancellationToken: token);

        RunResult ran = await mcp.Write.RunAsync(
            "echo wide-prompt-marker",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);

        Assert.Equal(0, ran.ExitStatus);
        Assert.Contains(
            ran.Output.Lines,
            line => line.Contains("wide-prompt-marker", StringComparison.Ordinal));
        Assert.DoesNotContain(ran.Output.Lines, line => line.Contains("lt_r_", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task Tailing_answers_only_what_is_new()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        // A first read establishes a position and spends nothing.
        TailResult start = await mcp.Read.TailPaneAsync(pane, cancellationToken: token);
        Assert.Empty(start.Content.Lines);
        Assert.False(start.LinesMissed);

        await mcp.Write.RunAsync("echo tail-one", pane, timeoutSeconds: 20, cancellationToken: token);
        TailResult first = await mcp.Read.TailPaneAsync(
            pane,
            start.Cursor,
            cancellationToken: token);
        Assert.Contains(first.Content.Lines, line => line.Contains("tail-one", StringComparison.Ordinal));

        // The same cursor advanced past that text, so asking again answers
        // nothing rather than the same lines a second time.
        TailResult again = await mcp.Read.TailPaneAsync(
            pane,
            first.Cursor,
            cancellationToken: token);
        Assert.DoesNotContain(again.Content.Lines, line => line.Contains("tail-one", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task A_cursor_from_another_pane_is_refused_rather_than_guessed_at()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        await Assert.ThrowsAsync<McpException>(
            () => mcp.Read.TailPaneAsync(
                scope.Pane.Id.ToString(),
                "not-a-cursor",
                cancellationToken: token));
    }

    [UnixFact]
    public async Task Waiting_ends_on_the_text_it_was_told_to_wait_for()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        await mcp.Write.SendKeysAsync(
            "(sleep 1; echo READY_MARKER)",
            pane,
            enter: true,
            cancellationToken: token);

        WaitResult matched = await mcp.Read.WaitForTextAsync(
            pane,
            ["READY_MARKER"],
            timeoutSeconds: 20,
            cancellationToken: token);
        Assert.Equal(WaitOutcome.Matched, matched.Outcome);
        Assert.Equal("READY_MARKER", matched.MatchedPattern);
    }

    [UnixFact]
    public async Task A_wait_that_finds_nothing_says_so_and_says_how_long_it_waited()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        WaitResult timedOut = await mcp.Read.WaitForTextAsync(
            scope.Pane.Id.ToString(),
            ["NEVER_APPEARS_ANYWHERE"],
            timeoutSeconds: 2,
            cancellationToken: token);

        Assert.Equal(WaitOutcome.Timeout, timedOut.Outcome);

        // An over-large request is lowered rather than refused, so the result
        // has to report what was actually used or the policy is invisible.
        Assert.Equal(2, timedOut.EffectiveTimeoutSeconds);
    }

    [UnixFact]
    public async Task An_over_large_timeout_is_lowered_to_the_ceiling()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create(new ServerPolicy
        {
            Tier = SafetyTier.Destructive,
            WaitCeiling = TimeSpan.FromSeconds(2),
        });
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        WaitResult capped = await mcp.Read.WaitForTextAsync(
            scope.Pane.Id.ToString(),
            ["NEVER_APPEARS_ANYWHERE"],
            timeoutSeconds: 600,
            cancellationToken: token);

        Assert.Equal(2, capped.EffectiveTimeoutSeconds);
    }

    [UnixFact]
    public async Task A_job_returns_at_once_and_is_collected_later()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        JobInfo started = await mcp.Write.StartJobAsync(
            "sleep 2; echo JOB_FINISHED",
            pane,
            cancellationToken: token);
        Assert.Equal(JobState.Running, started.State);

        JobReport report = await mcp.Write.JobAsync(
            started.JobId,
            waitSeconds: 20,
            cancellationToken: token);
        Assert.Equal(JobState.Exited, report.Job.State);
        Assert.Equal(0, report.Job.ExitStatus);
        Assert.Contains(
            report.Output.Lines,
            line => line.Contains("JOB_FINISHED", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task Convenience_tools_withdraw_owned_job_waiters_on_shutdown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            cancellationToken: token);
        WriteTools tools = McpTools.Writing(scope.Server);

        JobInfo started = await tools.StartJobAsync(
            "sleep 30",
            scope.Pane.Id.ToString(),
            cancellationToken: token);
        await tools.DisposeAsync().AsTask().WaitAsync(token);

        string channel = $"lt_r_{started.JobId}";
        await scope.Server.WaitForAsync(
            new WaitForRequest(channel, TmuxWaitMode.Signal),
            token);
        await using TmuxWaitChannel next = scope.Server.OpenWaitChannel(channel);

        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(1), token));
    }

    [UnixFact]
    public async Task A_job_handle_nobody_issued_is_refused_with_advice()
    {
        await using McpToolFixture mcp = McpToolFixture.Create();
        McpException unknown = Assert.Throws<McpException>(() => mcp.Jobs.Get("nope"));
        Assert.Contains("tmux_list_jobs", unknown.Message, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task Searching_finds_a_pane_by_what_it_is_showing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        await mcp.Write.RunAsync(
            "echo NEEDLE_IN_PANE",
            pane,
            timeoutSeconds: 20,
            cancellationToken: token);

        SearchResult found = await mcp.Read.SearchPanesAsync(
            "NEEDLE_IN_PANE",
            cancellationToken: token);
        Assert.Contains(found.Panes, match => match.PaneId == pane);
    }

    [UnixFact]
    public async Task A_pattern_that_cannot_be_compiled_is_refused_before_it_runs()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        McpException bad = await Assert.ThrowsAsync<McpException>(
            () => mcp.Read.SearchPanesAsync("([unclosed", cancellationToken: token));
        Assert.Contains("regular expression", bad.Message, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task Building_a_workspace_answers_the_ids_the_next_call_needs()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        ActionResult split = await mcp.Write.SplitPaneAsync(
            scope.Pane.Id.ToString(),
            PaneDirection.Below,
            cancellationToken: token);

        // The new pane's id comes back, so laying out a workspace does not
        // need a listing between every step.
        Assert.NotNull(split.PaneId);
        Assert.NotEqual(scope.Pane.Id.ToString(), split.PaneId);

        IReadOnlyList<PaneInfo> panes = await mcp.Read.ListPanesAsync(
            windowId: scope.Window.Id.ToString(),
            cancellationToken: token);
        Assert.Equal(2, panes.Count);
    }

    [UnixFact]
    public async Task Killing_removes_what_it_names_and_nothing_else()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        ActionResult split = await mcp.Write.SplitPaneAsync(
            scope.Pane.Id.ToString(),
            cancellationToken: token);
        await mcp.Destructive.KillPaneAsync(split.PaneId!, cancellationToken: token);

        IReadOnlyList<PaneInfo> left = await mcp.Read.ListPanesAsync(
            windowId: scope.Window.Id.ToString(),
            cancellationToken: token);
        Assert.Single(left);
        Assert.Equal(scope.Pane.Id.ToString(), left[0].PaneId);
    }

    [UnixFact]
    public async Task A_snapshot_describes_the_cursor_and_the_text_together()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);

        PaneSnapshot snapshot = await mcp.Read.SnapshotPaneAsync(
            scope.Pane.Id.ToString(),
            cancellationToken: token);

        Assert.Equal(scope.Pane.Id.ToString(), snapshot.Pane.PaneId);
        Assert.NotNull(snapshot.CursorY);
        Assert.False(snapshot.AlternateScreen);
    }

    [UnixFact]
    public async Task A_capture_keeps_the_newest_lines_and_reports_the_rest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using McpToolFixture mcp = McpToolFixture.Create();
        TmuxTestFactory factory = new();
        await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(
            mcp.Options,
            token);
        string pane = scope.Pane.Id.ToString();

        await mcp.Write.RunAsync("seq 1 300", pane, timeoutSeconds: 30, cancellationToken: token);

        CaptureResult small = await mcp.Read.CapturePaneAsync(
            pane,
            includeHistory: true,
            maxLines: 10,
            cancellationToken: token);

        Assert.Equal(10, small.Content.Lines.Count);
        Assert.True(small.Content.Truncated);
        Assert.True(small.Content.DroppedLines > 0);

        // A terminal's newest line is the one that says what happened, so a
        // budget that kept the oldest would answer the wrong question.
        Assert.Contains(small.Content.Lines, line => line.Contains("300", StringComparison.Ordinal));
    }
}
