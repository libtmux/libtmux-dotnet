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
            "%1", ["ready"], timeoutSeconds: 20, progress: progress, cancellationToken: cancellation.Token);

        await Task.WhenAny(fixture.Clock.Waiting.Task, waiting).WaitAsync(cancellation.Token);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(7, fixture.Captures);
        ManualTimer timer = await fixture.Clock.Waiting.Task;
        Assert.True(timer.DueTime > TimeSpan.Zero);
        Assert.True(timer.DueTime < TimeSpan.FromSeconds(20));
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
            Assert.Equal(20, result.EffectiveTimeoutSeconds);
            Assert.False(result.PollingFallback);
            Assert.Equal(8, fixture.Captures); // Only the final result tail is read.
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
            PaneReader.ReadVisibleAsync(pane, null, McpPaneReader.Failure, token));

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
        internal bool Unstable { get; set; } = true;
        internal int UnstableThroughCapture { get; init; } = int.MaxValue;
        internal bool ReadyAfterBaseline { get; init; }
        internal bool NotifyLayoutChanges { get; init; }
        internal string Output { get; set; } = "baseline";
        internal McpException? CaptureFailure { get; init; }

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
                payload = string.Concat(projection.Fields.Select(field => Field(field.WireName)
                    + FormatProjection.RowSeparator)) + "\n";
            }
            else if (arguments.Contains("capture-pane", StringComparer.Ordinal))
            {
                Captures++;
                if (Captures > 1 && CaptureFailure is not null)
                    throw CaptureFailure;
                if (Unstable && Captures <= UnstableThroughCapture && (!StableEntry || Captures > 1))
                {
                    _height = _height == 24 ? 25 : 24;
                    if (NotifyLayoutChanges)
                        await Control.EmitAndObserveAsync(new TmuxNotificationEvent("layout-change", ["@1"]));
                }
                payload = Output + "\n";
                if (Captures == 1 && ReadyAfterBaseline)
                    Output = "ready";
            }
            else if (arguments.Any(value => value.Contains("#{history_size}", StringComparison.Ordinal)))
            {
                payload = $"123\t0\t2000\t{_height}\t0\t0\t0\n";
            }
            else
            {
                throw new InvalidOperationException($"Unexpected command: {string.Join(' ', arguments)}");
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
        public IAsyncEnumerable<TmuxEvent> Events => ReadEventsAsync();
        public bool IsRunning { get; private set; } = true;
        public Task<IReadOnlyList<string>> SendAsync(TmuxCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        internal void Emit(TmuxEvent value) => _events.Writer.TryWrite((value, null));
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
        }
        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            _events.Writer.TryComplete();
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
