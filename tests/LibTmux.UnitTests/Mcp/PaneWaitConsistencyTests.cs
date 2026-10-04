using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LibTmux.Internal;
using LibTmux.Mcp;
using LibTmux.UnitTests.Connection;
using ModelContextProtocol;

namespace LibTmux.UnitTests.Mcp;

[UnsupportedOSPlatform("windows")]
public sealed class PaneWaitConsistencyTests
{
    [Fact]
    public void A_core_wait_request_owns_patterns_and_validates_before_io()
    {
        Regex[] wanted = [new("ready")];
        Regex[] stopped = [new("fatal")];
        PaneWaitRequest request = new()
        {
            Patterns = wanted,
            StopPatterns = stopped,
        };

        wanted[0] = new("changed");
        stopped[0] = new("changed");
        Assert.Equal("ready", Assert.Single(request.Patterns).ToString());
        Assert.Equal("fatal", Assert.Single(request.StopPatterns).ToString());
        request.Validate();

        Assert.Throws<ArgumentException>(() => PaneWaitRequest.FromTextPatterns(
            ["(?=not-supported)"]));
    }

    [Fact]
    public void Native_backtracking_regex_cannot_consume_a_longer_budget_than_the_wait()
    {
        PaneWaitRequest request = new()
        {
            Patterns = [new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromSeconds(5))],
            Timeout = TimeSpan.FromMilliseconds(25),
        };
        PaneWaitPattern[] patterns = request.Snapshot().Wanted;
        int work = 0;
        Stopwatch elapsed = Stopwatch.StartNew();

        Assert.Throws<RegexMatchTimeoutException>(() => PaneTextWaiter.Match(
            patterns, [new string('a', 10_000) + "x"], ref work,
            TestContext.Current.CancellationToken,
            () => request.Timeout - elapsed.Elapsed));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_core_matching_work_limit_maps_to_an_mcp_error()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new()
        {
            Output = new string('x', 600_000),
            Unstable = false,
        };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));
        PaneWaitRequest request = PaneWaitRequest.FromTextPatterns(
            [.. Enumerable.Range(0, 15).Select(index => $"absent-{index}")]);

        PaneTextWaiter.MatchWorkExceededException exceeded =
            await Assert.ThrowsAsync<PaneTextWaiter.MatchWorkExceededException>(() =>
                WaitCoreAsync(observer, pane, request, token));

        McpException mapped = ReadTools.WaitMatchingError(exceeded);
        Assert.Contains("matching work limit", mapped.Message, StringComparison.Ordinal);
        Assert.Same(exceeded, mapped.InnerException);
    }

    [Fact]
    public async Task A_core_text_wait_reports_a_pattern_already_on_the_rendered_screen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "ready" };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using (PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock))
        {
            PaneWaitResult result = await WaitCoreAsync(observer,
                pane,
                PaneWaitRequest.FromTextPatterns(["ready"]),
                token);

            Assert.Equal(PaneWaitOutcome.PresentAtEntry, result.Outcome);
            Assert.Contains("ready", result.Tail);
            Assert.False(result.PollingFallback);
        }

        Assert.False(fixture.Control.IsRunning);
    }

    [Fact]
    public async Task A_core_text_wait_tail_keeps_a_ready_line_above_unused_grid_padding()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "ready on 8080", PadToHeight = true };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));

        PaneWaitResult result = await WaitCoreAsync(observer,
            pane,
            PaneWaitRequest.FromTextPatterns(["ready on"]) with { TailLines = 4 },
            token);

        Assert.Equal(PaneWaitOutcome.PresentAtEntry, result.Outcome);
        Assert.Contains("ready on 8080", result.Tail);
        Assert.Equal(0, result.OmittedTailLines);
    }

    [Fact]
    public async Task A_core_text_wait_gives_an_existing_stop_pattern_priority()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "ready fatal" };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));

        PaneWaitResult result = await WaitCoreAsync(observer,
            pane,
            PaneWaitRequest.FromTextPatterns(["ready"], ["fatal"]),
            token);

        Assert.Equal(PaneWaitOutcome.Stopped, result.Outcome);
        Assert.Equal("fatal", result.Pattern);
    }

    [Fact]
    public async Task A_trigger_wait_sends_after_baseline_even_when_old_screen_matches()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "old ready fatal", Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);
        bool sent = false;

        PaneWaitResult result = await PaneTextWaiter.WaitAsync(
            observer, pane, PaneWaitRequest.FromTextPatterns(["ready"], ["fatal"]).Snapshot(),
            null, null, null, token, afterEntry: _ =>
            {
                Assert.True(fixture.Captures > 0);
                sent = true;
                fixture.Output = "old ready fatal\nnew ready";
                fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "new ready"));
                return Task.CompletedTask;
            });

        Assert.True(sent);
        Assert.Equal(PaneWaitOutcome.Matched, result.Outcome);
        Assert.Equal("ready", result.Pattern);
        Assert.Contains("new ready", result.Tail);
    }

    [Fact]
    public async Task A_typed_trigger_wait_keeps_an_empty_delta_without_rebasing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "baseline", Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);
        Func<IReadOnlyList<string>, IReadOnlyList<string>> withoutEcho =
            PaneText.TypedEchoRemover("typed");

        Task<PaneWaitResult> waiting = PaneTextWaiter.WaitAsync(
            observer, pane,
            (PaneWaitRequest.FromTextPatterns(["ready"]) with
            { Timeout = TimeSpan.FromSeconds(1) }).Snapshot(),
            withoutEcho, withoutEcho, null, token,
            afterEntry: _ => Task.CompletedTask);
        await fixture.Clock.Waiting.Task.WaitAsync(token);
        Assert.Equal(2, fixture.Captures);
        Assert.False(waiting.IsCompleted);

        fixture.Output = "ready";
        fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "ready"));
        PaneWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneWaitOutcome.Matched, result.Outcome);
        Assert.Equal("ready", result.Pattern);
        Assert.False(result.LinesMissed);
        Assert.False(result.AnchorLost);
    }

    [Fact]
    public async Task A_trigger_wait_does_not_match_old_screen_on_final_capture()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "old ready fatal", Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);
        bool sent = false;

        Task<PaneWaitResult> waiting = PaneTextWaiter.WaitAsync(
            observer, pane,
            (PaneWaitRequest.FromTextPatterns(["ready"], ["fatal"]) with
            { Timeout = TimeSpan.FromMilliseconds(500) }).Snapshot(),
            null, null, null, token, afterEntry: _ =>
            {
                sent = true;
                return Task.CompletedTask;
            });
        Task completed = await Task.WhenAny(waiting, fixture.Clock.Waiting.Task).WaitAsync(token);
        Assert.Same(fixture.Clock.Waiting.Task, completed);
        Assert.True(sent);

        ManualTimer timer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        timer.Fire();
        PaneWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneWaitOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task A_trigger_wait_recovers_after_an_ambiguous_anchor_loss()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new()
        {
            Output = "old ready fatal",
            Unstable = false,
            HistorySize = 100,
        };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);
        bool sent = false;

        Task<PaneWaitResult> waiting = PaneTextWaiter.WaitAsync(
            observer, pane, PaneWaitRequest.FromTextPatterns(["ready"], ["fatal"]).Snapshot(),
            null, null, null, token, afterEntry: _ =>
            {
                sent = true;
                fixture.HistorySize = 0;
                fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "unrecoverable prior row"));
                return Task.CompletedTask;
            });
        Task completed = await Task.WhenAny(waiting, fixture.Clock.Waiting.Task).WaitAsync(token);
        PaneWaitResult? completedWait = waiting.IsCompletedSuccessfully ? await waiting : null;
        string premature = completedWait is not null
            ? $"Outcome={completedWait.Outcome}, AnchorLost={completedWait.AnchorLost}, "
                + $"LinesMissed={completedWait.LinesMissed}"
            : $"Status={waiting.Status}, Error={waiting.Exception?.GetBaseException().Message}";
        Assert.True(ReferenceEquals(fixture.Clock.Waiting.Task, completed), premature);
        Assert.True(sent);

        fixture.Output = "old ready fatal\nnew ready";
        fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "new ready"));
        PaneWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneWaitOutcome.Matched, result.Outcome);
        Assert.Equal("ready", result.Pattern);
        Assert.True(result.LinesMissed);
        Assert.True(result.AnchorLost);
    }

    [Fact]
    public async Task A_trigger_wait_ignores_old_matches_in_an_incrementally_rewritten_row()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "old ready fatal", Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneWaitResult> waiting = PaneTextWaiter.WaitAsync(
            observer, pane, PaneWaitRequest.FromTextPatterns(["ready"], ["fatal"]).Snapshot(),
            null, null, null, token, afterEntry: _ =>
            {
                fixture.Output = "old ready fatal re";
                fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "re"));
                return Task.CompletedTask;
            });
        Task completed = await Task.WhenAny(waiting, fixture.Clock.Waiting.Task).WaitAsync(token);
        PaneWaitResult? premature = waiting.IsCompletedSuccessfully ? await waiting : null;
        Assert.True(ReferenceEquals(fixture.Clock.Waiting.Task, completed),
            premature is null ? $"Wait status: {waiting.Status}" :
            $"Wait ended early: {premature.Outcome}, pattern {premature.Pattern}");

        fixture.Output = "old ready fatal ready";
        fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "ady"));
        PaneWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneWaitOutcome.Matched, result.Outcome);
        Assert.Equal("ready", result.Pattern);
    }

    [Fact]
    public async Task A_proven_trigger_final_delta_survives_an_unstable_tail_capture()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new()
        {
            Output = "baseline",
            Unstable = false,
            PauseAfterGridStateReadNumber = 6,
        };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneWaitResult> waiting = PaneTextWaiter.WaitAsync(
            observer, pane,
            (PaneWaitRequest.FromTextPatterns(["ready"]) with
            { Timeout = TimeSpan.FromSeconds(1) }).Snapshot(),
            null, null, null, token, afterEntry: _ => Task.CompletedTask);
        ManualTimer timer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.Output = "ready";
        timer.Fire();
        await fixture.GridStateReadReached.Task.WaitAsync(token);
        fixture.Unstable = true;
        fixture.GridStateReadRelease.TrySetResult();

        PaneWaitResult result = await waiting.WaitAsync(token);
        Assert.Equal(PaneWaitOutcome.Matched, result.Outcome);
        Assert.Equal("ready", result.Pattern);
        Assert.Contains("ready", result.Tail);
    }

    [Fact]
    public async Task A_confirmed_trigger_match_survives_final_echo_projection_budget_failure()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new()
        {
            Output = "baseline",
            Unstable = false,
            ChangeOutputAfterCaptureNumber = 2,
            OutputAfterCapture = new string('x', PaneWaitRequest.MaximumMatchWorkBytes + 1),
        };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        PaneWaitResult result = await PaneTextWaiter.WaitAsync(
            observer, pane, PaneWaitRequest.FromTextPatterns(["ready"]).Snapshot(),
            null, null, null, token, afterEntry: _ =>
            {
                fixture.Output = "ready";
                fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "ready"));
                return Task.CompletedTask;
            }, typedEcho: "sent");

        Assert.Equal(PaneWaitOutcome.Matched, result.Outcome);
        Assert.Equal("ready", result.Pattern);
        Assert.Contains("ready", result.Tail);
        Assert.True(result.LinesMissed);
    }

    [Fact]
    public async Task Mcp_wait_gives_an_existing_stop_pattern_priority()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "ready fatal", Unstable = false };

        WaitResult result = await fixture.Tools.WaitForTextAsync(
            "%1", ["ready"], stopPatterns: ["fatal"], cancellationToken: token);

        Assert.Equal(WaitOutcome.Stopped, result.Outcome);
        Assert.Equal("fatal", result.MatchedPattern);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_core_text_wait_wakes_for_new_rendered_output(bool namedPattern)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);
        PaneWaitRequest request = PaneWaitRequest.FromTextPatterns(
            namedPattern ? ["ready"] : null) with
        { Timeout = TimeSpan.FromSeconds(20) };

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer, pane, request, token);
        await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.Output = "ready";
        fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "raw fragment"));
        PaneWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(
            namedPattern ? PaneWaitOutcome.Matched : PaneWaitOutcome.AnyOutput,
            result.Outcome);
        Assert.Contains("ready", result.Tail);
        Assert.False(result.PollingFallback);
    }

    [Fact]
    public async Task A_core_text_wait_times_out_without_polling_capture()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer,
            pane,
            PaneWaitRequest.FromTextPatterns(["ready"]) with { Timeout = TimeSpan.FromSeconds(1) },
            token);
        ManualTimer timer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        int capturesBeforeTimeout = fixture.Captures;
        timer.Fire();
        PaneWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneWaitOutcome.TimedOut, result.Outcome);
        Assert.Equal(capturesBeforeTimeout + 1, fixture.Captures);
        Assert.False(result.PollingFallback);
    }

    [Fact]
    public async Task A_final_stable_capture_prevents_a_false_plain_timeout()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer,
            pane, PaneWaitRequest.FromTextPatterns(["ready"]), token);
        ManualTimer timer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.Output = "ready";
        timer.Fire();
        PaneWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneWaitOutcome.Matched, result.Outcome);
        Assert.Equal("ready", result.Pattern);
        Assert.Contains("ready", result.Tail);
    }

    [Fact]
    public async Task Output_after_the_reserved_final_read_still_matches_before_deadline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new()
        {
            Unstable = false,
            PauseAfterGridStateReadNumber = 6,
        };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer,
            pane,
            PaneWaitRequest.FromTextPatterns(["late-ready"]) with { Timeout = TimeSpan.FromSeconds(1) },
            token);
        ManualTimer timer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        timer.Fire();
        await fixture.GridStateReadReached.Task.WaitAsync(token);
        fixture.Output = "late-ready";
        fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "new output"));
        fixture.GridStateReadRelease.TrySetResult();

        PaneWaitResult result = await waiting.WaitAsync(token);
        Assert.Equal(PaneWaitOutcome.Matched, result.Outcome);
        Assert.Equal("late-ready", result.Pattern);
        Assert.Contains("late-ready", result.Tail);
    }

    [Fact]
    public async Task An_early_final_timer_keeps_waiting_on_the_same_output_signal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneWaitResult> waiting = PaneTextWaiter.WaitAsync(
            observer, pane,
            (PaneWaitRequest.FromTextPatterns(["ready"]) with
            { Timeout = TimeSpan.FromSeconds(1) }).Snapshot(),
            null, null, null, token, finalTimerProvider: fixture.Clock);
        ManualTimer observationTimer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        observationTimer.Fire();
        ManualTimer earlyFinalTimer = await fixture.Clock.WaitingAgain.Task.WaitAsync(token);
        earlyFinalTimer.Fire();

        fixture.Output = "ready";
        fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "ready"));
        PaneWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneWaitOutcome.Matched, result.Outcome);
        Assert.Equal("ready", result.Pattern);
        Assert.False(result.PollingFallback);
    }

    [Fact]
    public async Task A_final_grid_delta_satisfies_any_output_after_a_lost_event()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer,
            pane, new PaneWaitRequest { Timeout = TimeSpan.FromSeconds(20) }, token);
        ManualTimer timer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.Output = "new rendered output";
        timer.Fire();
        PaneWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneWaitOutcome.AnyOutput, result.Outcome);
        Assert.Contains("new rendered output", result.Tail);
    }

    [Fact]
    public async Task A_final_grid_delta_keeps_the_line_that_triggered_any_output()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new()
        {
            Unstable = false,
            ChangeOutputAfterCaptureNumber = 3,
            OutputAfterCapture = "between final reads",
        };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer,
            pane, new PaneWaitRequest { Timeout = TimeSpan.FromSeconds(20) }, token);
        ManualTimer timer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        timer.Fire();
        PaneWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneWaitOutcome.AnyOutput, result.Outcome);
        Assert.Contains("between final reads", result.Tail);
    }

    [Fact]
    public async Task A_confirmed_entry_match_survives_pane_disappearance_during_final_capture()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new()
        {
            Output = "ready",
            DisappearAtGridStateRead = 3,
        };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));

        PaneWaitResult result = await WaitCoreAsync(observer,
            pane, PaneWaitRequest.FromTextPatterns(["ready"]), token);

        Assert.Equal(PaneWaitOutcome.PresentAtEntry, result.Outcome);
        Assert.Contains("ready", result.Tail);
    }

    [Fact]
    public async Task A_core_text_wait_reports_a_dead_process_with_its_last_screen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "last line", Dead = true };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));

        PaneWaitResult result = await WaitCoreAsync(observer,
            pane, PaneWaitRequest.FromTextPatterns(["never"]), token);

        Assert.Equal(PaneWaitOutcome.PaneExited, result.Outcome);
        Assert.Contains("last line", result.Tail);
    }

    [Fact]
    public async Task A_core_text_wait_reports_original_process_replacement_as_pane_died()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "before respawn", Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer,
            pane, PaneWaitRequest.FromTextPatterns(["never"]), token);
        await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.PanePid = "456";
        fixture.Output = "new process";
        fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "new process"));
        PaneWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneWaitOutcome.PaneExited, result.Outcome);
        Assert.Contains("before respawn", result.Tail);
        Assert.DoesNotContain("new process", result.Tail);
    }

    [Fact]
    public async Task A_fallback_visible_read_rejects_a_replaced_process()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { PanePid = "456", Output = "replacement text" };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PaneReader.ReadVisibleAsync(pane, "123",
                (failure, _) => new InvalidOperationException(failure.ToString()), token));
        Assert.Equal("Replaced", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_wait_rejects_a_pane_unlinked_from_its_observed_session(bool notificationLost)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer,
            pane, PaneWaitRequest.FromTextPatterns(["ready"]), token);
        await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.LinkedInCapturedSession = false;
        fixture.Output = "ready";
        fixture.Control.Emit(notificationLost
            ? new TmuxEventsDroppedEvent(1, 1)
            : new TmuxNotificationEvent("window-close", ["@1"]));

        TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(() => waiting);
        Assert.Contains("session $1", error.Message, StringComparison.Ordinal);
        Assert.Contains("pane %1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_explicit_fallback_discloses_observation_after_session_unlink()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer,
            pane, PaneWaitRequest.FromTextPatterns(["ready"]) with
            { AllowPollingFallback = true }, token);
        await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.LinkedInCapturedSession = false;
        fixture.Output = "ready";
        fixture.Control.Emit(new TmuxNotificationEvent("window-close", ["@1"]));
        PaneWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneWaitOutcome.Matched, result.Outcome);
        Assert.True(result.PollingFallback);
        Assert.True(result.LinesMissed);
    }

    [Fact]
    public async Task An_attach_fallback_reports_that_grid_lines_may_have_been_missed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "ready", Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub activity = new((_, _) =>
            Task.FromException<IControlModeSession>(new LibTmuxException("control unavailable")));

        PaneWaitResult result = await WaitCoreAsync(activity, pane,
            PaneWaitRequest.FromTextPatterns(["ready"]) with
            { AllowPollingFallback = true }, token);

        Assert.Equal(PaneWaitOutcome.PresentAtEntry, result.Outcome);
        Assert.True(result.PollingFallback);
        Assert.True(result.LinesMissed);
    }

    [Fact]
    public async Task Disposing_an_activity_hub_cancels_a_stalled_control_start()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new();
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IControlModeSession> startup = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PaneActivityHub observer = new((_, _) =>
        {
            entered.TrySetResult();
            return startup.Task;
        });

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer, pane, cancellationToken: token);
        await entered.Task.WaitAsync(token);
        await observer.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        startup.SetResult(fixture.Control);
        await fixture.Control.Disposed.Task.WaitAsync(token);
        Assert.False(fixture.Control.IsRunning);
    }

    [Fact]
    public async Task Disposing_an_activity_hub_cancels_a_stalled_grid_capture()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { BlockFirstCapture = true };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer, pane, cancellationToken: token);
        await fixture.CaptureStarted.Task.WaitAsync(token);
        await observer.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(fixture.Control.IsRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_wait_deadline_covers_control_start_and_baseline_capture(bool stalledCapture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { BlockFirstCapture = stalledCapture };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        TaskCompletionSource<IControlModeSession> startup = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using PaneActivityHub observer = new((_, _) => stalledCapture
            ? Task.FromResult<IControlModeSession>(fixture.Control)
            : startup.Task);

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer,
            pane,
            new PaneWaitRequest { Timeout = TimeSpan.FromMilliseconds(100) },
            token);
        if (stalledCapture)
        {
            await fixture.CaptureStarted.Task.WaitAsync(token);
        }

        PaneWaitResult result = await waiting.WaitAsync(token);
        Assert.Equal(PaneWaitOutcome.TimedOut, result.Outcome);
        Assert.InRange(result.Elapsed, TimeSpan.FromMilliseconds(75), TimeSpan.FromSeconds(1));

        if (!stalledCapture)
        {
            startup.SetResult(fixture.Control);
            await fixture.Control.Disposed.Task.WaitAsync(token);
        }
    }

    [Fact]
    public async Task A_faulted_control_event_stream_reports_observation_loss()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneWaitResult> waiting = WaitCoreAsync(observer,
            pane, PaneWaitRequest.FromTextPatterns(["never"]), token);
        await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.Control.Fail(new IOException("control stream closed"));

        TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(() => waiting);
        Assert.Contains("control client", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pane_removed_between_state_and_capture_reports_pane_died()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { DisappearsAtFirstCapture = true };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneActivityHub observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));

        PaneWaitResult result = await WaitCoreAsync(observer,
            pane, PaneWaitRequest.FromTextPatterns(["never"]), token);

        Assert.Equal(PaneWaitOutcome.PaneExited, result.Outcome);
        Assert.Null(await fixture.Server.FindPaneAsync(pane.Id, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unstable_wait_keeps_its_deadline_and_cancellation_without_polling(bool cancel)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using Fixture fixture = new();
        RecordingProgress progress = new();
        Task<WaitResult> waiting = fixture.Tools.WaitForTextAsync(
            "%1", ["ready"], timeoutSeconds: 1, progress: progress,
            cancellationToken: cancellation.Token);

        await Task.WhenAny(fixture.Clock.Waiting.Task, waiting).WaitAsync(cancellation.Token);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(7, fixture.Captures);
        ManualTimer timer = await fixture.Clock.Waiting.Task;
        Assert.True(timer.DueTime > TimeSpan.Zero);
        Assert.True(timer.DueTime < TimeSpan.FromSeconds(1));
        Assert.NotNull(progress.Last);
        Assert.Equal("waiting on %1", progress.Last.Message);

        if (cancel)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(7, fixture.Captures);
        }
        else
        {
            timer.Fire();
            WaitResult result = await waiting;
            Assert.Equal(WaitOutcome.Timeout, result.Outcome);
            Assert.Equal(1, result.EffectiveTimeoutSeconds);
            Assert.False(result.PollingFallback);
            Assert.Equal(10, fixture.Captures); // Three bounded final stability attempts.
        }

        Assert.False(fixture.Activity.IsStreaming);
    }

    [Fact]
    public async Task An_unstable_wait_resumes_only_when_its_control_signal_fires()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new();
        Task<WaitResult> waiting = fixture.Tools.WaitForTextAsync(
            "%1", ["ready"], timeoutSeconds: 20, cancellationToken: token);
        await Task.WhenAny(fixture.Clock.Waiting.Task, waiting).WaitAsync(token);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(7, fixture.Captures);

        fixture.Unstable = false;
        fixture.Output = "ready";
        fixture.Control.Emit(new TmuxOutputEvent(new PaneId(1), "ready"));
        WaitResult result = await waiting;

        Assert.Equal(WaitOutcome.Matched, result.Outcome);
        Assert.Contains("ready", result.Tail.Lines);
        Assert.False(result.PollingFallback);
        Assert.Equal(9, fixture.Captures);
        Assert.False(fixture.Activity.IsStreaming);
    }

    [Fact]
    public async Task Mcp_wait_discloses_a_lost_grid_anchor_without_control_event_loss()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Unstable = false, HistorySize = 100 };
        Task<WaitResult> waiting = fixture.Tools.WaitForTextAsync(
            "%1", ["ready"], timeoutSeconds: 20, cancellationToken: token);
        await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.HistorySize = 0;
        fixture.Output = "ready";
        fixture.Control.Emit(new TmuxNotificationEvent("layout-change", ["@1"]));
        WaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(WaitOutcome.Matched, result.Outcome);
        Assert.True(result.LinesMissed);
        Assert.True(result.AnchorLost);
        Assert.Equal(0, result.EventsDropped);
    }

    [Fact]
    public async Task Mcp_any_output_wait_ignores_an_unchanged_screen_after_anchor_loss()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new()
        {
            Output = "old line",
            Unstable = false,
            HistorySize = 100,
        };
        Task<WaitResult> waiting = fixture.Tools.WaitForTextAsync(
            "%1", patterns: null, timeoutSeconds: 20, cancellationToken: token);
        await fixture.Clock.Waiting.Task.WaitAsync(token);

        fixture.HistorySize = 0;
        fixture.Control.Emit(new TmuxNotificationEvent("layout-change", ["@1"]));
        Task completed = await Task.WhenAny(waiting, fixture.Clock.WaitingAgain.Task).WaitAsync(token);
        WaitResult? premature = waiting.IsCompletedSuccessfully ? await waiting : null;
        Assert.True(ReferenceEquals(fixture.Clock.WaitingAgain.Task, completed),
            premature is null ? $"Wait status: {waiting.Status}" :
            $"Wait ended early: {premature.Outcome}, AnchorLost={premature.AnchorLost}");

        fixture.Output = "old line\nfresh line";
        fixture.Control.Emit(new TmuxOutputEvent(new PaneId(1), "fresh line"));
        WaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(WaitOutcome.AnyOutput, result.Outcome);
        Assert.Contains("fresh line", result.Tail.Lines);
        Assert.True(result.LinesMissed);
        Assert.True(result.AnchorLost);
    }

    [Fact]
    public async Task A_wait_retries_after_resize_only_instability_without_later_output()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new()
        {
            ReadyAfterBaseline = true,
            UnstableThroughCapture = 7,
            NotifyLayoutChanges = true,
        };
        Task<WaitResult> waiting = fixture.Tools.WaitForTextAsync(
            "%1", ["ready"], timeoutSeconds: 20, cancellationToken: token);

        Task completed = await Task.WhenAny(waiting, fixture.Clock.Waiting.Task).WaitAsync(token);
        Assert.Same(waiting, completed);
        WaitResult result = await waiting;

        Assert.Equal(WaitOutcome.Matched, result.Outcome);
        Assert.Contains("ready", result.Tail.Lines);
        Assert.False(result.PollingFallback);
        Assert.Equal(9, fixture.Captures);
        Assert.False(fixture.Activity.IsStreaming);
    }

    [Fact]
    public async Task A_direct_unstable_capture_still_refuses_after_bounded_attempts()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { StableEntry = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));

        McpException failure = await Assert.ThrowsAsync<McpPaneReader.UnstableSnapshotException>(() =>
            McpPaneReader.ReadVisibleAsync(pane, null, token));

        Assert.Contains("changed during every snapshot attempt", failure.Message, StringComparison.Ordinal);
        Assert.Equal(3, fixture.Captures);
    }

    [Fact]
    public async Task A_wait_does_not_retry_unrelated_capture_errors()
    {
        McpException failure = new("The capture was refused.");
        await using Fixture fixture = new() { CaptureFailure = failure };

        McpException observed = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Tools.WaitForTextAsync("%1", ["ready"], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(failure, observed);
        Assert.Equal(2, fixture.Captures);
        Assert.False(fixture.Activity.IsStreaming);
    }

    private static Task<PaneWaitResult> WaitCoreAsync(
        PaneActivityHub activity,
        Pane pane,
        PaneWaitRequest? request = null,
        CancellationToken cancellationToken = default) =>
        PaneTextWaiter.WaitAsync(activity, pane, (request ?? new PaneWaitRequest()).Snapshot(),
            null, null, null, cancellationToken);

    private sealed class RecordingProgress : IProgress<ProgressNotificationValue>
    {
        internal ProgressNotificationValue? Last { get; private set; }
        public void Report(ProgressNotificationValue value) => Last = value;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly ServerGeneration Generation = new(71, 701);
        private readonly TmuxConnectionAccessor _accessor;
        private int _height = 24;

        internal Fixture()
        {
            Server = new Server(new TmuxConnection(
                new ServerConnectionOptions { SocketName = "wait-consistency" },
                FakeMultiplexer.AnsweringVersion(ExecuteAsync)), Generation, "tmux 3.7");
            _accessor = new(Server);
            Activity = new((_, _) => Task.FromResult<IControlModeSession>(Control), timeProvider: Clock);
            Tools = new(_accessor, new ServerPolicy(), Activity);
        }

        internal Server Server { get; }
        internal PaneActivityHub Activity { get; }
        internal ReadTools Tools { get; }
        internal ControlledClock Clock { get; } = new();
        internal QuietControl Control { get; } = new();
        internal int Captures { get; private set; }
        internal bool StableEntry { get; init; } = true;
        internal bool Dead { get; init; }
        internal string PanePid { get; set; } = "123";
        internal bool PadToHeight { get; init; }
        internal bool LinkedInCapturedSession { get; set; } = true;
        internal bool Unstable { get; set; } = true;
        internal int UnstableThroughCapture { get; init; } = int.MaxValue;
        internal bool ReadyAfterBaseline { get; init; }
        internal int ChangeOutputAfterCaptureNumber { get; init; }
        internal string? OutputAfterCapture { get; init; }
        internal bool NotifyLayoutChanges { get; init; }
        internal string Output { get; set; } = "baseline";
        internal McpException? CaptureFailure { get; init; }
        internal bool BlockFirstCapture { get; init; }
        internal bool DisappearsAtFirstCapture { get; init; }
        internal bool PaneExists { get; set; } = true;
        internal int DisappearAtGridStateRead { get; init; }
        internal int PauseAfterGridStateReadNumber { get; init; }
        internal int GridStateReads { get; private set; }
        internal TaskCompletionSource GridStateReadReached { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource GridStateReadRelease { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal int HistorySize { get; set; }
        internal TaskCompletionSource CaptureStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CaptureRelease { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            await Activity.DisposeAsync().ConfigureAwait(false);
            _accessor.Dispose();
        }

        private async Task<TmuxCommandResult> ExecuteAsync(TmuxCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<string> arguments = request.LogicalArguments;
            string payload;
            if (arguments.Contains("list-panes", StringComparer.Ordinal))
            {
                FormatProjection projection = FormatProjection.Create("list-panes", TmuxVersion.Parse("3.7"));
                payload = !PaneExists || arguments.Contains("-s", StringComparer.Ordinal)
                    && !LinkedInCapturedSession
                    ? string.Empty
                    : string.Concat(projection.Fields.Select(field => Field(field.WireName)
                        + FormatProjection.RowSeparator)) + "\n";
            }
            else if (arguments.Contains("capture-pane", StringComparer.Ordinal))
            {
                Captures++;
                if (DisappearsAtFirstCapture && Captures == 1)
                {
                    PaneExists = false;
                    byte[] error = Encoding.UTF8.GetBytes("can't find pane: %1\n");
                    return new TmuxCommandResult(arguments, 1, ReadOnlyMemory<byte>.Empty,
                        error, [], ["can't find pane: %1"]);
                }

                if (BlockFirstCapture && Captures == 1)
                {
                    CaptureStarted.TrySetResult();
                    await CaptureRelease.Task.WaitAsync(token);
                }

                if (Captures > 1 && CaptureFailure is not null)
                    throw CaptureFailure;
                if (Unstable && Captures <= UnstableThroughCapture && (!StableEntry || Captures > 1))
                {
                    _height = _height == 24 ? 25 : 24;
                    if (NotifyLayoutChanges)
                        await Control.EmitAndObserveAsync(new TmuxNotificationEvent("layout-change", ["@1"]));
                }
                payload = PadToHeight
                    ? Output + new string('\n', _height)
                    : Output + "\n";
                if (Captures == ChangeOutputAfterCaptureNumber)
                {
                    Output = OutputAfterCapture!;
                }

                if (Captures == 1 && ReadyAfterBaseline)
                    Output = "ready";
            }
            else if (arguments.Contains("display-message", StringComparer.Ordinal)
                && arguments.Any(value => value.Contains(FormatProjection.RowSeparator, StringComparison.Ordinal)))
            {
                FormatProjection projection = FormatProjection.Create("list-panes", TmuxVersion.Parse("3.7"));
                payload = string.Concat(projection.Fields.Select(field =>
                    (PaneExists || field.WireName is "pid" or "start_time"
                        ? Field(field.WireName)
                        : string.Empty)
                    + FormatProjection.RowSeparator)) + "\n";
            }
            else if (arguments.Any(value => value.Contains("#{history_size}", StringComparison.Ordinal)))
            {
                GridStateReads++;
                if (GridStateReads == DisappearAtGridStateRead)
                {
                    PaneExists = false;
                }

                payload = PaneExists
                    ? $"{PanePid}\t{HistorySize}\t2000\t{_height}\t0\t{(Dead ? 1 : 0)}\t0\n"
                    : string.Empty;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected command: {string.Join(' ', arguments)}");
            }

            if (PauseAfterGridStateReadNumber > 0
                && arguments.Any(value => value.Contains("#{history_size}", StringComparison.Ordinal))
                && !arguments.Any(value => value.Contains(FormatProjection.RowSeparator, StringComparison.Ordinal))
                && GridStateReads == PauseAfterGridStateReadNumber)
            {
                GridStateReadReached.TrySetResult();
                await GridStateReadRelease.Task.WaitAsync(token);
            }

            byte[] output = Encoding.UTF8.GetBytes($"{Generation.ProcessId}:{Generation.StartTime}\n{payload}");
            return new TmuxCommandResult(arguments, 0, output, ReadOnlyMemory<byte>.Empty,
                Utf8BackslashDecoder.ProjectOutputLines(output), []);
        }

        private static string Field(string name) => name switch
        {
            "pid" => Generation.ProcessId.ToString(CultureInfo.InvariantCulture),
            "start_time" => Generation.StartTime.ToString(CultureInfo.InvariantCulture),
            "session_id" => "$1",
            "window_id" => "@1",
            "pane_id" => "%1",
            "pane_width" => "80",
            "pane_height" => "24",
            "pane_active" => "1",
            _ => string.Empty,
        };
    }

    private sealed class QuietControl : IControlModeSession
    {
        private readonly Channel<(TmuxEvent Event, TaskCompletionSource? Observed)> _events =
            Channel.CreateUnbounded<(TmuxEvent, TaskCompletionSource?)>();
        private Exception? _failure;
        public IAsyncEnumerable<TmuxEvent> Events => ReadEventsAsync();
        public bool IsRunning { get; private set; } = true;
        internal TaskCompletionSource Disposed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<string>> SendAsync(TmuxCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        internal void Emit(TmuxEvent value) => _events.Writer.TryWrite((value, null));
        internal void Fail(Exception error)
        {
            _failure = error;
            _events.Writer.TryComplete();
        }
        internal Task EmitAndObserveAsync(TmuxEvent value)
        {
            TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_events.Writer.TryWrite((value, observed)));
            return observed.Task;
        }

        private async IAsyncEnumerable<TmuxEvent> ReadEventsAsync()
        {
            await foreach ((TmuxEvent value, TaskCompletionSource? observed) in _events.Reader.ReadAllAsync())
            {
                yield return value;
                observed?.TrySetResult();
            }

            if (_failure is not null)
            {
                throw _failure;
            }
        }
        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            _events.Writer.TryComplete();
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledClock : TimeProvider
    {
        private int _timersCreated;
        internal TaskCompletionSource<ManualTimer> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ManualTimer> WaitingAgain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(callback, state, dueTime);
            Waiting.TrySetResult(timer);
            if (Interlocked.Increment(ref _timersCreated) == 2)
                WaitingAgain.TrySetResult(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        internal TimeSpan DueTime { get; } = dueTime;
        internal void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
