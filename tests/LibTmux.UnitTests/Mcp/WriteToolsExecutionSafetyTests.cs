using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;
using LibTmux.Mcp;
using LibTmux.UnitTests.Connection;
using ModelContextProtocol;

namespace LibTmux.UnitTests.Mcp;

[UnsupportedOSPlatform("windows")]
public sealed class WriteToolsExecutionSafetyTests
{
    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void Pane_synchronization_parser_accepts_canonical_values(string raw, bool expected) =>
        Assert.Equal(expected, ParsePaneSynchronization(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("on")]
    [InlineData("2")]
    public void Pane_synchronization_parser_rejects_noncanonical_values(string? raw)
    {
        McpException failure = Assert.Throws<McpException>(
            () => CapabilityTools.ParsePaneSynchronization(raw, "%test-pane"));
        Assert.Contains("%test-pane", failure.Message, StringComparison.Ordinal);
        Assert.Contains("0 or 1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capability_send_refuses_a_modal_peer_with_one_preflight()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "1", "0"), new PaneListingRow("%2", "1", "1")],
            ],
        };

        McpException failure = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Capabilities.SendKeysAsync(
                "echo must-not-send", "%1", cancellationToken: token));

        Assert.Contains("%2", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.SuccessfulSends);
        Assert.Equal(1, fixture.PaneListingCount);
    }

    [Fact]
    public async Task Capability_batch_refuses_a_modal_peer_with_one_preflight()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "1", "0"), new PaneListingRow("%2", "1", "1")],
            ],
        };

        PaneInputBatchResult batch = await fixture.Capabilities.SendKeysBatchAsync(
            [new PaneInputOperation("echo must-not-send", "%1", Enter: true)],
            cancellationToken: token);

        PaneInputOperationResult refusal = Assert.Single(batch.Results);
        Assert.False(refusal.Success);
        Assert.Contains("%2", refusal.Error, StringComparison.Ordinal);
        Assert.Equal(0, fixture.SuccessfulSends);
        Assert.Equal(1, fixture.PaneListingCount);
    }

    [Fact]
    public async Task Capability_send_returns_membership_from_final_preflight()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "1", "0"), new PaneListingRow("%2", "1", "0")],
            ],
        };

        PaneInputResult sent = await fixture.Capabilities.SendKeysAsync(
            "echo changed", "%1", cancellationToken: token);

        Assert.Equal(["%1", "%2"], sent.TargetPaneIds);
        Assert.Equal(1, fixture.PaneListingCount);
    }

    [Fact]
    public async Task Capability_run_reaches_one_pane_from_inside_a_cohort()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "1", "0"), new PaneListingRow("%2", "1", "0")],
                [new PaneListingRow("%1", "1", "0"), new PaneListingRow("%2", "1", "0")],
            ],
        };

        // The payload travels through a buffer, which tmux does not fan out,
        // so a synchronized cohort no longer costs the caller the tool. The
        // dispatch-time listing still happens: the source has to be re-read
        // for the mode and liveness checks even though the cohort cannot
        // change the outcome.
        RunResult result = await fixture.Capabilities.RunShellCommandAsync(
            "echo one-pane", "%1", cancellationToken: token);

        Assert.Equal("%1", result.PaneId);
        Assert.Equal(1, fixture.SuccessfulSends);
        Assert.Equal(2, fixture.PaneListingCount);
    }

    [Fact]
    public async Task Batch_rejects_every_invalid_shape_before_query_or_mutation()
    {
        await using var fixture = new ToolFixture(
            new ServerPolicy
            {
                MaxBytes = 4_000,
                WaitCeiling = TimeSpan.FromSeconds(1),
            });
        IReadOnlyList<IReadOnlyList<KeyStep>> invalid =
        [
            [],
            Enumerable.Range(0, 65).Select(_ => new KeyStep("x")).ToArray(),
            [null!],
            [new KeyStep(null!)],
            [new KeyStep(new string('x', 4_001))],
            [new KeyStep("x", DelayMilliseconds: 2_001)],
            [
                new KeyStep("a", DelayMilliseconds: 600),
                new KeyStep("b", DelayMilliseconds: 600),
            ],
        ];

        foreach (IReadOnlyList<KeyStep> steps in invalid)
        {
            _ = await Assert.ThrowsAnyAsync<Exception>(() =>
                fixture.Tools.SendKeysBatchAsync(
                    steps,
                    paneId: "%1",
                    cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.Empty(fixture.Commands);
        Assert.Equal(0, fixture.SuccessfulSends);
    }

    private static bool ParsePaneSynchronization(string raw) =>
        CapabilityTools.ParsePaneSynchronization(raw, "%test-pane");

    [Fact]
    public async Task Batch_second_step_not_dispatched_reports_one_prior_mutation_as_unknown()
    {
        await using var fixture = new ToolFixture { FailSendAttempt = 2 };

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            fixture.Tools.SendKeysBatchAsync(
                [new KeyStep("first"), new KeyStep("second")],
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Contains("do not retry", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.SuccessfulSends);
        Assert.Equal(2, fixture.SendAttempts);
    }

    [Fact]
    public async Task Batch_ambiguous_first_step_is_normalized_to_unknown()
    {
        await using var fixture = new ToolFixture { AmbiguousSendAttempt = 1 };

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            fixture.Tools.SendKeysBatchAsync(
                [new KeyStep("first")],
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Contains("do not retry", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.SuccessfulSends);
    }

    [Fact]
    public async Task Batch_cancellation_during_delay_after_a_step_is_unknown()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var fixture = new ToolFixture
        {
            CancelAfterSuccessfulSend = cancellation,
        };

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            fixture.Tools.SendKeysBatchAsync(
                [new KeyStep("first", DelayMilliseconds: 1_000)],
                paneId: "%1",
                cancellationToken: cancellation.Token));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Equal(1, fixture.SuccessfulSends);
    }

    [Fact]
    public async Task Run_reads_only_output_after_its_bound_baseline()
    {
        await using var fixture = new ToolFixture
        {
            BeforeLines = ["old screen"],
            AfterLines = ["old screen", "fresh output"],
        };

        RunResult result = await fixture.Tools.RunAsync(
            "echo fresh output",
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("fresh output", result.Output.Lines);
        Assert.DoesNotContain("old screen", result.Output.Lines);
        Assert.False(result.LinesMissed);
        Assert.False(result.AnchorLost);
        string payload = Assert.Single(
            fixture.Commands.SelectMany(static arguments => arguments),
            argument => argument.Contains("run-shell", StringComparison.Ordinal));
        Assert.Contains("'run-shell' '-b' '-d' '90'", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("sleep ", payload, StringComparison.Ordinal);
        Assert.Contains(fixture.Commands, IsStatusUnset);
    }

    [Fact]
    public async Task Run_honors_an_explicit_nonactive_pane_target()
    {
        await using var fixture = new ToolFixture
        {
            PaneListings =
            [
                [new PaneListingRow("%1", "0", "0"), new PaneListingRow("%2", "0", "0")],
            ],
        };

        RunResult result = await fixture.Tools.RunAsync(
            "echo target two",
            paneId: "%2",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("%2", result.PaneId);
        Assert.Contains(fixture.Commands, arguments =>
            IsSendKeys(arguments) && arguments.Contains("%2", StringComparer.Ordinal));
        Assert.Contains(fixture.Commands, arguments =>
            arguments.Contains("capture-pane", StringComparer.Ordinal)
            && arguments.Contains("%2", StringComparer.Ordinal));
        Assert.DoesNotContain(fixture.Commands, arguments =>
            (IsSendKeys(arguments) || arguments.Contains("capture-pane", StringComparer.Ordinal))
            && arguments.Contains("%1", StringComparer.Ordinal));
    }

    [Fact]
    public async Task Run_rejects_an_oversized_command_before_any_query_or_mutation()
    {
        await using var fixture = new ToolFixture(new ServerPolicy { MaxBytes = 4_000 });

        McpException failure = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Tools.RunAsync(
                new string('x', 4_001),
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("longer script in a file", failure.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task Unstable_first_tail_returns_no_cursor_and_a_later_stable_read_recovers()
    {
        await using var fixture = new ToolFixture();
        fixture.DestabilizeNextStateSamples(6);

        McpException failure = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Reads.TailPaneAsync(
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("every snapshot attempt", failure.Message, StringComparison.Ordinal);
        Assert.Equal(6, fixture.StateSampleCount);
        Assert.DoesNotContain(fixture.Commands, IsSendKeys);

        TailResult recovered = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith("tmux-tail-v3:", recovered.Cursor, StringComparison.Ordinal);
        Assert.False(recovered.LinesMissed);
        Assert.False(recovered.AnchorLost);
    }

    [Fact]
    public async Task Tail_cursor_fingerprints_the_same_capture_the_call_observed()
    {
        await using var fixture = new ToolFixture
        {
            CaptureSequence = [["progress 10%"], ["progress 20%"]],
        };

        TailResult initial = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        TailResult next = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: initial.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("progress 20%", next.Content.Lines);
        Assert.Equal(2, fixture.CaptureCount);
    }

    [Fact]
    public async Task Tail_cursor_uses_the_rebased_origin_after_history_eviction()
    {
        string[] newLines = [.. Enumerable.Range(0, 35).Select(static index => $"new {index}")];
        string[] changedCapture =
        [
            .. Enumerable.Range(0, 90).Select(static index => $"history {index}"),
            .. Enumerable.Range(0, 4).Select(static index => $"visible {index}"),
            "old cursor",
            .. newLines,
        ];
        await using var fixture = new ToolFixture
        {
            CaptureSequence =
            [
                [
                    .. Enumerable.Range(0, 39).Select(static index => $"before {index}"),
                    "old cursor",
                ],
                changedCapture,
                changedCapture,
            ],
            StateSequence = [new StateSample(90, 100, 40, 39)],
        };

        TailResult initial = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        TailResult changed = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: initial.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);
        TailResult idle = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: changed.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(newLines, changed.Content.Lines);
        Assert.Empty(idle.Content.Lines);
        Assert.Equal(3, fixture.CaptureCount);
    }

    [Fact]
    public async Task Tail_cursor_upward_redraw_is_reported_once()
    {
        string[] redraw = ["rewritten cursor", "new middle", "new bottom"];
        await using var fixture = new ToolFixture
        {
            CaptureSequence =
            [
                ["before 0", "before 1", "before 2", "old cursor"],
                redraw,
                redraw,
            ],
            StateSequence =
            [
                new StateSample(0, 50_000, 4, 3),
                new StateSample(0, 50_000, 4, 3),
                new StateSample(0, 50_000, 4, 1),
                new StateSample(0, 50_000, 4, 1),
                new StateSample(0, 50_000, 4, 1),
                new StateSample(0, 50_000, 4, 1),
            ],
        };

        TailResult initial = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        TailResult changed = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: initial.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);
        TailResult idle = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: changed.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(redraw, changed.Content.Lines);
        Assert.Empty(idle.Content.Lines);
        Assert.Equal(3, fixture.CaptureCount);
    }

    [Fact]
    public async Task Tail_idle_read_skips_the_entire_suffix_below_its_cursor()
    {
        string[] staticRows =
        [.. Enumerable.Range(0, 40).Select(static index => $"static {index}")];
        await using var fixture = new ToolFixture
        {
            CaptureSequence = [staticRows, staticRows],
            StateSequence = [new StateSample(0, 50_000, 40, 0)],
        };

        TailResult initial = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        TailResult idle = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: initial.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(idle.Content.Lines);
        Assert.Equal(2, fixture.CaptureCount);
    }

    [Fact]
    public async Task Wait_for_any_output_does_not_match_an_idle_suffix()
    {
        string[] staticRows =
        [.. Enumerable.Range(0, 40).Select(static index => $"static {index}")];
        await using var fixture = new ToolFixture(
            new ServerPolicy { WaitCeiling = TimeSpan.FromSeconds(1) })
        {
            CaptureSequence = [staticRows, staticRows, staticRows],
            StateSequence = [new StateSample(0, 50_000, 40, 0)],
        };

        WaitResult result = await fixture.Reads.WaitForTextAsync(
            paneId: "%1",
            timeoutSeconds: 0.2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WaitOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task Read_since_busy_retry_falls_back_to_a_new_stable_cursor()
    {
        await using var fixture = new ToolFixture();
        TailResult initial = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cancellationToken: TestContext.Current.CancellationToken);
        fixture.DestabilizeNextStateSamples(6);

        TailResult recovered = await fixture.Reads.TailPaneAsync(
            paneId: "%1",
            cursor: initial.Cursor,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(recovered.LinesMissed);
        Assert.True(recovered.AnchorLost);
        Assert.NotEqual(initial.Cursor, recovered.Cursor);
    }

    [Fact]
    public async Task Run_does_not_dispatch_when_its_baseline_never_stabilizes()
    {
        await using var fixture = new ToolFixture();
        fixture.DestabilizeNextStateSamples(6);

        McpException failure = await Assert.ThrowsAsync<McpException>(() =>
            fixture.Tools.RunAsync(
                "echo never",
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("every snapshot attempt", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Commands, IsSendKeys);
        Assert.DoesNotContain(fixture.Commands, IsStatusUnset);
    }

    [Fact]
    public async Task Run_post_dispatch_failure_is_unknown_and_cleans_its_marker()
    {
        await using var fixture = new ToolFixture { FailWait = true };

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            fixture.Tools.RunAsync(
                "echo once",
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Contains("do not retry", failure.Message, StringComparison.OrdinalIgnoreCase);

        // Two: the key that clears whatever was typed at the prompt, then the
        // payload. The payload itself is one dispatch — sending keys put the
        // text and its Enter in separate commands, which is why "the text was
        // sent but Enter failed" is a state this library has to describe.
        Assert.Equal(2, fixture.SuccessfulSends);
        Assert.Contains(fixture.Commands, IsStatusUnset);
    }

    [Fact]
    public async Task Run_cancellation_uses_an_independent_marker_cleanup_token()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var fixture = new ToolFixture
        {
            CancelDuringWait = cancellation,
        };

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            fixture.Tools.RunAsync(
                "echo once",
                paneId: "%1",
                cancellationToken: cancellation.Token));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.False(fixture.StatusUnsetTokenWasCancelled);
        Assert.Contains(fixture.Commands, IsStatusUnset);
    }

    [Fact]
    public async Task Run_unknown_send_dispatch_still_attempts_independent_cleanup()
    {
        await using var fixture = new ToolFixture { UnknownSendAttempt = 1 };

        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(() =>
            fixture.Tools.RunAsync(
                "echo maybe",
                paneId: "%1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Contains(fixture.Commands, IsStatusUnset);
    }

    private static bool IsStatusUnset(string[] arguments) =>
        arguments.Contains("set-option", StringComparer.Ordinal)
        && arguments.Contains("-u", StringComparer.Ordinal)
        && arguments.Any(static argument => argument.StartsWith("@lt_s_", StringComparison.Ordinal));

    // The run delivers through a buffer and send_keys through keys, so a
    // payload reaching a pane is either.
    private static bool IsSendKeys(string[] arguments) =>
        arguments.Contains("send-keys", StringComparer.Ordinal)
        || arguments.Contains("paste-buffer", StringComparer.Ordinal);

    private sealed record StateSample(
        int HistorySize,
        int HistoryLimit,
        int PaneHeight,
        int CursorY);

    private sealed record PaneListingRow(string Id, string Synchronized, string InMode);

    private sealed class ToolFixture : IAsyncDisposable
    {
        private static readonly ServerGeneration Generation = new(121, 1201);

        private readonly TmuxConnectionAccessor _accessor;
        private readonly PaneActivityHub _activity;
        private readonly object _stateGate = new();
        private int _captureCount;
        private int _paneListingCount;
        private int _runStarted;
        private int _stateSampleCount;
        private int _stateVersion;
        private int _unstableStateSamples;

        internal ToolFixture(ServerPolicy? policy = null)
        {
            _activity = new PaneActivityHub(static (_, _) =>
                Task.FromException<IControlModeSession>(
                    new InvalidOperationException("Fake control attach unavailable.")));
            var connection = new TmuxConnection(
                new ServerConnectionOptions(socketName: "execution-safety"),
                FakeMultiplexer.AnsweringVersion(ExecuteAsync));
            var server = new Server(connection, Generation, "tmux 3.7");
            _accessor = new TmuxConnectionAccessor(server);
            ServerPolicy effectivePolicy = policy ?? new ServerPolicy();
            Tools = new WriteTools(
                _accessor,
                effectivePolicy,
                _activity);
            Reads = new ReadTools(_accessor, effectivePolicy, _activity);
            Capabilities = new CapabilityTools(
                Reads,
                Tools,
                _accessor,
                CapabilityRegistry.All(),
                effectivePolicy);
        }

        internal IReadOnlyList<string> AfterLines { get; init; } = ["fresh output"];

        internal int? AmbiguousSendAttempt { get; init; }

        internal IReadOnlyList<string> BeforeLines { get; init; } = ["prompt"];

        internal CancellationTokenSource? CancelAfterSuccessfulSend { get; init; }

        internal CancellationTokenSource? CancelDuringWait { get; init; }

        internal int CaptureCount => Volatile.Read(ref _captureCount);

        internal IReadOnlyList<IReadOnlyList<string>>? CaptureSequence { get; init; }

        internal ConcurrentQueue<string[]> Commands { get; } = new();

        internal int? FailSendAttempt { get; init; }

        internal bool FailWait { get; init; }

        internal int SendAttempts { get; private set; }

        internal IReadOnlyList<StateSample>? StateSequence { get; init; }

        internal int StateSampleCount
        {
            get
            {
                lock (_stateGate)
                {
                    return _stateSampleCount;
                }
            }
        }

        internal int SuccessfulSends { get; private set; }

        internal bool StatusUnsetTokenWasCancelled { get; private set; }

        internal int? UnknownSendAttempt { get; init; }

        internal CapabilityTools Capabilities { get; }

        internal IReadOnlyList<IReadOnlyList<PaneListingRow>>? PaneListings { get; init; }

        internal int PaneListingCount => Volatile.Read(ref _paneListingCount);

        internal WriteTools Tools { get; }

        internal ReadTools Reads { get; }

        internal void DestabilizeNextStateSamples(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            lock (_stateGate)
            {
                _unstableStateSamples = count;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _activity.DisposeAsync().ConfigureAwait(false);
            _accessor.Dispose();
        }

        private Task<TmuxCommandResult> ExecuteAsync(
            TmuxCommandRequest request,
            CancellationToken cancellationToken)
        {
            string[] arguments = [.. request.LogicalArguments];
            Commands.Enqueue(arguments);
            if (arguments.Length > 0 && arguments[0] == "wait-for")
            {
                if (CancelDuringWait is not null)
                {
                    CancelDuringWait.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (FailWait)
                {
                    throw new TmuxTransportException(
                        "wait was not dispatched",
                        arguments,
                        TmuxDispatchState.NotDispatched);
                }
            }

            if (IsSendKeys(arguments))
            {
                SendAttempts++;
                if (AmbiguousSendAttempt == SendAttempts)
                {
                    throw new TmuxOperationCanceledException(
                        "send may have executed",
                        cancellationToken,
                        commandMayHaveExecuted: true,
                        clientProcessId: 1201);
                }

                if (FailSendAttempt == SendAttempts)
                {
                    throw new TmuxTransportException(
                        "send was not dispatched",
                        arguments,
                        TmuxDispatchState.NotDispatched);
                }

                if (UnknownSendAttempt == SendAttempts)
                {
                    throw new TmuxTransportException(
                        "send dispatch is unknown",
                        arguments,
                        TmuxDispatchState.Unknown);
                }

                SuccessfulSends++;
                Volatile.Write(ref _runStarted, 1);
                CancelAfterSuccessfulSend?.Cancel();
            }

            if (IsStatusUnset(arguments))
            {
                StatusUnsetTokenWasCancelled |= cancellationToken.IsCancellationRequested;
            }

            return Task.FromResult(Success(arguments, Output(arguments)));
        }

        private string Output(IReadOnlyList<string> arguments)
        {
            string body = arguments.Contains("list-panes", StringComparer.Ordinal)
                ? PaneListing()
                : arguments.Any(static argument => argument.Contains(
                    "#{history_size}",
                    StringComparison.Ordinal))
                    ? StateListing()
                    : arguments.Contains("capture-pane", StringComparer.Ordinal)
                        ? Lines(CaptureLines())
                        : arguments.Contains("show-options", StringComparer.Ordinal)
                            ? $"{arguments[^1]} 0\n"
                            : string.Empty;
            return IsGuarded(arguments)
                ? $"{Generation.ProcessId}:{Generation.StartTime}\n{body}"
                : body;
        }

        private static string Lines(IReadOnlyList<string> lines) =>
            lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n";

        private IReadOnlyList<string> CaptureLines()
        {
            int index = Interlocked.Increment(ref _captureCount) - 1;
            if (CaptureSequence is { Count: > 0 } sequence)
            {
                return sequence[Math.Min(index, sequence.Count - 1)];
            }

            return Volatile.Read(ref _runStarted) == 0 ? BeforeLines : AfterLines;
        }

        private string StateListing()
        {
            StateSample state;
            lock (_stateGate)
            {
                int sampleIndex = _stateSampleCount;
                _stateSampleCount++;
                if (StateSequence is { Count: > 0 } sequence)
                {
                    state = sequence[Math.Min(sampleIndex, sequence.Count - 1)];
                }
                else
                {
                    if (_unstableStateSamples > 0)
                    {
                        _unstableStateSamples--;
                        _stateVersion++;
                    }

                    int cursorY = Volatile.Read(ref _runStarted) == 0 ? 0 : 1;
                    state = new StateSample(_stateVersion, 50_000, 24, cursorY);
                }
            }

            return $"4242\t{state.HistorySize}\t{state.HistoryLimit}\t"
                + $"{state.PaneHeight}\t{state.CursorY}\t0\t0\n";
        }

        private static bool IsGuarded(IReadOnlyList<string> arguments) =>
            arguments.Count > 2
            && arguments[0] == "display-message"
            && arguments[2] == "#{pid}:#{start_time}";

        private string PaneListing()
        {
            FormatProjection projection = FormatProjection.Create(
                "list-panes",
                TmuxVersion.Parse("3.7"));
            int index = Interlocked.Increment(ref _paneListingCount) - 1;
            IReadOnlyList<PaneListingRow> panes = PaneListings is { Count: > 0 } sequence
                ? sequence[Math.Min(index, sequence.Count - 1)]
                : [new PaneListingRow("%1", "0", "0")];
            return string.Concat(panes.Select(pane => string.Concat(projection.Fields.Select(
                field => FieldValue(field.WireName, pane) + FormatProjection.RowSeparator)) + "\n"));
        }

        private static string FieldValue(string field, PaneListingRow pane) => field switch
        {
            "pid" => Generation.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "start_time" => Generation.StartTime.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "session_id" => "$1",
            "window_id" => "@1",
            "pane_id" => pane.Id,
            "pane_pid" => "4242",
            "pane_width" => "80",
            "pane_height" => "24",
            "pane_active" => pane.Id == "%1" ? "1" : "0",
            "pane_in_mode" => pane.InMode,
            "pane_synchronized" => pane.Synchronized,
            _ => string.Empty,
        };

        private static TmuxCommandResult Success(
            IReadOnlyList<string> arguments,
            string standardOutput)
        {
            byte[] output = Encoding.UTF8.GetBytes(standardOutput);
            return new TmuxCommandResult(
                arguments,
                0,
                output,
                ReadOnlyMemory<byte>.Empty,
                Utf8BackslashDecoder.ProjectOutputLines(output),
                []);
        }
    }
}
