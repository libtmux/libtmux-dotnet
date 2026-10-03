using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
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
        string[] wanted = ["ready"];
        string[] stopped = ["fatal"];
        PaneTextWaitRequest request = new()
        {
            Patterns = wanted,
            StopPatterns = stopped,
        };

        wanted[0] = "changed";
        stopped[0] = "changed";
        Assert.Equal("ready", Assert.Single(request.Patterns!));
        Assert.Equal("fatal", Assert.Single(request.StopPatterns!));
        request.Validate();

        PaneTextWaitRequest invalid = new() { Patterns = ["(?=not-supported)"] };
        Assert.Throws<NotSupportedException>(invalid.Validate);
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
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));
        PaneTextWaitRequest request = new()
        {
            Patterns = [.. Enumerable.Range(0, 15).Select(index => $"absent-{index}")],
        };

        PaneTextWaitEngine.MatchWorkExceededException exceeded =
            await Assert.ThrowsAsync<PaneTextWaitEngine.MatchWorkExceededException>(() =>
                observer.WaitForTextAsync(pane, request, token));

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
        await using (PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock))
        {
            PaneTextWaitResult result = await observer.WaitForTextAsync(
                pane,
                new PaneTextWaitRequest { Patterns = ["ready"] },
                token);

            Assert.Equal(PaneTextWaitOutcome.PresentAtEntry, result.Outcome);
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
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));

        PaneTextWaitResult result = await observer.WaitForTextAsync(
            pane,
            new PaneTextWaitRequest { Patterns = ["ready on"], TailLines = 4 },
            token);

        Assert.Equal(PaneTextWaitOutcome.PresentAtEntry, result.Outcome);
        Assert.Contains("ready on 8080", result.Tail);
        Assert.Equal(0, result.OmittedTailLines);
    }

    [Fact]
    public async Task A_core_text_wait_gives_an_existing_stop_pattern_priority()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "ready fatal" };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));

        PaneTextWaitResult result = await observer.WaitForTextAsync(
            pane,
            new PaneTextWaitRequest
            {
                Patterns = ["ready"],
                StopPatterns = ["fatal"],
            },
            token);

        Assert.Equal(PaneTextWaitOutcome.Stopped, result.Outcome);
        Assert.Equal("fatal", result.MatchedPattern);
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
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);
        PaneTextWaitRequest request = new()
        {
            Patterns = namedPattern ? ["ready"] : null,
            Timeout = TimeSpan.FromSeconds(20),
        };

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(pane, request, token);
        await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.Output = "ready";
        fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "raw fragment"));
        PaneTextWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(
            namedPattern ? PaneTextWaitOutcome.Matched : PaneTextWaitOutcome.AnyOutput,
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
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(
            pane,
            new PaneTextWaitRequest
            {
                Patterns = ["ready"],
                Timeout = TimeSpan.FromSeconds(1),
            },
            token);
        ManualTimer timer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        int capturesBeforeTimeout = fixture.Captures;
        timer.Fire();
        PaneTextWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneTextWaitOutcome.TimedOut, result.Outcome);
        Assert.Equal(capturesBeforeTimeout + 1, fixture.Captures);
        Assert.False(result.PollingFallback);
    }

    [Fact]
    public async Task A_final_stable_capture_prevents_a_false_plain_timeout()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(
            pane, new PaneTextWaitRequest { Patterns = ["ready"] }, token);
        ManualTimer timer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.Output = "ready";
        timer.Fire();
        PaneTextWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneTextWaitOutcome.Matched, result.Outcome);
        Assert.Equal("ready", result.MatchedPattern);
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
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(
            pane,
            new PaneTextWaitRequest
            {
                Patterns = ["late-ready"],
                Timeout = TimeSpan.FromSeconds(1),
            },
            token);
        ManualTimer timer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        timer.Fire();
        await fixture.GridStateReadReached.Task.WaitAsync(token);
        fixture.Output = "late-ready";
        fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "new output"));
        fixture.GridStateReadRelease.TrySetResult();

        PaneTextWaitResult result = await waiting.WaitAsync(token);
        Assert.Equal(PaneTextWaitOutcome.Matched, result.Outcome);
        Assert.Equal("late-ready", result.MatchedPattern);
        Assert.Contains("late-ready", result.Tail);
    }

    [Fact]
    public async Task A_final_grid_delta_satisfies_any_output_after_a_lost_event()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(
            pane, new PaneTextWaitRequest { Timeout = TimeSpan.FromSeconds(20) }, token);
        ManualTimer timer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.Output = "new rendered output";
        timer.Fire();
        PaneTextWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneTextWaitOutcome.AnyOutput, result.Outcome);
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
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(
            pane, new PaneTextWaitRequest { Timeout = TimeSpan.FromSeconds(20) }, token);
        ManualTimer timer = await fixture.Clock.Waiting.Task.WaitAsync(token);
        timer.Fire();
        PaneTextWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneTextWaitOutcome.AnyOutput, result.Outcome);
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
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));

        PaneTextWaitResult result = await observer.WaitForTextAsync(
            pane, new PaneTextWaitRequest { Patterns = ["ready"] }, token);

        Assert.Equal(PaneTextWaitOutcome.PresentAtEntry, result.Outcome);
        Assert.Contains("ready", result.Tail);
    }

    [Fact]
    public async Task A_core_text_wait_reports_a_dead_process_with_its_last_screen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "last line", Dead = true };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));

        PaneTextWaitResult result = await observer.WaitForTextAsync(
            pane, new PaneTextWaitRequest { Patterns = ["never"] }, token);

        Assert.Equal(PaneTextWaitOutcome.PaneDied, result.Outcome);
        Assert.Contains("last line", result.Tail);
    }

    [Fact]
    public async Task A_core_text_wait_reports_original_process_replacement_as_pane_died()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Output = "before respawn", Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(
            pane, new PaneTextWaitRequest { Patterns = ["never"] }, token);
        await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.PanePid = "456";
        fixture.Output = "new process";
        fixture.Control.Emit(new TmuxOutputEvent(pane.Id, "new process"));
        PaneTextWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneTextWaitOutcome.PaneDied, result.Outcome);
        Assert.Contains("before respawn", result.Tail);
        Assert.DoesNotContain("new process", result.Tail);
    }

    [Fact]
    public async Task A_fallback_visible_read_rejects_a_replaced_process()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { PanePid = "456", Output = "replacement text" };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));

        await Assert.ThrowsAsync<PaneTextGridReader.PaneReplacedException>(() =>
            PaneTextGridReader.ReadVisibleAsync(pane, "123", token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_wait_rejects_a_pane_unlinked_from_its_observed_session(bool notificationLost)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { Unstable = false };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(
            pane, new PaneTextWaitRequest { Patterns = ["ready"] }, token);
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
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            allowPollingFallback: true,
            timeProvider: fixture.Clock);

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(
            pane, new PaneTextWaitRequest { Patterns = ["ready"] }, token);
        await fixture.Clock.Waiting.Task.WaitAsync(token);
        fixture.LinkedInCapturedSession = false;
        fixture.Output = "ready";
        fixture.Control.Emit(new TmuxNotificationEvent("window-close", ["@1"]));
        PaneTextWaitResult result = await waiting.WaitAsync(token);

        Assert.Equal(PaneTextWaitOutcome.Matched, result.Outcome);
        Assert.True(result.PollingFallback);
        Assert.True(result.LinesMissed);
    }

    [Fact]
    public async Task Disposing_a_text_observer_cancels_a_stalled_control_start()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new();
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IControlModeSession> startup = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PaneTextObserver observer = new((_, _) =>
        {
            entered.TrySetResult();
            return startup.Task;
        });

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(pane, cancellationToken: token);
        await entered.Task.WaitAsync(token);
        await observer.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        startup.SetResult(fixture.Control);
        await fixture.Control.Disposed.Task.WaitAsync(token);
        Assert.False(fixture.Control.IsRunning);
    }

    [Fact]
    public async Task Disposing_a_text_observer_cancels_a_stalled_grid_capture()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Fixture fixture = new() { BlockFirstCapture = true };
        Pane pane = Assert.Single(await fixture.Server.GetPanesAsync(token));
        PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(pane, cancellationToken: token);
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
        await using PaneTextObserver observer = new((_, _) => stalledCapture
            ? Task.FromResult<IControlModeSession>(fixture.Control)
            : startup.Task);

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(
            pane,
            new PaneTextWaitRequest { Timeout = TimeSpan.FromMilliseconds(100) },
            token);
        if (stalledCapture)
        {
            await fixture.CaptureStarted.Task.WaitAsync(token);
        }

        PaneTextWaitResult result = await waiting.WaitAsync(token);
        Assert.Equal(PaneTextWaitOutcome.TimedOut, result.Outcome);
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
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control),
            timeProvider: fixture.Clock);

        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(
            pane, new PaneTextWaitRequest { Patterns = ["never"] }, token);
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
        await using PaneTextObserver observer = new(
            (_, _) => Task.FromResult<IControlModeSession>(fixture.Control));

        PaneTextWaitResult result = await observer.WaitForTextAsync(
            pane, new PaneTextWaitRequest { Patterns = ["never"] }, token);

        Assert.Equal(PaneTextWaitOutcome.PaneDied, result.Outcome);
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

        McpException failure = await Assert.ThrowsAnyAsync<McpException>(() =>
            PaneReader.ReadVisibleAsync(pane, null, token));

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
        internal TaskCompletionSource<ManualTimer> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(callback, state, dueTime);
            Waiting.TrySetResult(timer);
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
