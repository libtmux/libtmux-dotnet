using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Snapshots;

[UnsupportedOSPlatform("windows")]
public sealed class SnapshotAcquisitionTests
{
    private static readonly ServerGeneration Generation = new(91, 901);

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task Undefined_depth_is_rejected_before_dispatch(int value)
    {
        int calls = 0;
        Server server = CreateServer(() => calls++);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => server.CaptureSnapshotAsync((SnapshotDepth)value, TestContext.Current.CancellationToken));

        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(SnapshotDepth.Server)]
    [InlineData(SnapshotDepth.Sessions)]
    [InlineData(SnapshotDepth.Windows)]
    [InlineData(SnapshotDepth.Panes)]
    public async Task Precancelled_capture_never_dispatches(SnapshotDepth depth)
    {
        int calls = 0;
        Server server = CreateServer(() => calls++);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => server.CaptureSnapshotAsync(depth, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(SnapshotDepth.Server)]
    [InlineData(SnapshotDepth.Sessions)]
    [InlineData(SnapshotDepth.Windows)]
    [InlineData(SnapshotDepth.Panes)]
    public async Task Acquisition_records_monotonic_duration_and_depth_without_hidden_reads(SnapshotDepth depth)
    {
        var fixture = new AcquisitionFixture();
        var clock = new AcquisitionClock();
        DateTimeOffset started = clock.Utc;
        fixture.Replied = _ => clock.Advance();
        Assert.Null(fixture.Server.SnapshotMetadata);

        Server captured = await fixture.Server.CaptureSnapshotAsync(depth, clock, TestContext.Current.CancellationToken);

        SnapshotMetadata metadata = Assert.IsType<SnapshotMetadata>(captured.SnapshotMetadata);
        int reads = Math.Max(1, (int)depth);
        Assert.Equal(reads, fixture.Commands.Count);
        Assert.Equal(depth, metadata.Depth);
        Assert.Equal(Generation, metadata.Generation);
        Assert.Equal(captured.Generation, metadata.Generation);
        Assert.Equal(started, metadata.StartedAtUtc);
        Assert.Equal(started.AddHours(-reads), metadata.CompletedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(reads), metadata.Elapsed);
        fixture.RefuseDispatch = true;
        Assert.Equal(depth >= SnapshotDepth.Sessions, captured.Sessions.IsCaptured);
        Assert.Equal(depth >= SnapshotDepth.Windows, captured.Windows.IsCaptured);
        Assert.Equal(depth >= SnapshotDepth.Panes, captured.Panes.IsCaptured);
        if (captured.Sessions.IsCaptured)
        {
            Session[] selected = [.. captured.Sessions.Where(session => session.Id == new SessionId(0))];
            Assert.Single(selected);
            Assert.Same(captured, selected[0].Server);
        }
        else
        {
            Assert.Throws<IncompleteSnapshotException>(() => captured.Sessions.Count);
        }
        if (captured.Windows.IsCaptured)
        {
            Session session = Assert.Single(captured.Sessions);
            Window window = Assert.Single(session.Windows);
            Assert.Multiple(
                () => Assert.Same(captured, window.Server),
                () => Assert.Same(session, window.Session),
                () => Assert.Same(window, session.ActiveWindow.Value));
        }
        if (captured.Panes.IsCaptured)
        {
            Window window = Assert.Single(captured.Windows);
            Pane pane = Assert.Single(window.Panes);
            Assert.Multiple(
                () => Assert.Same(captured, pane.Server),
                () => Assert.Same(window, pane.Window),
                () => Assert.Same(window.Session, pane.Session),
                () => Assert.Same(pane, window.ActivePane.Value),
                () => Assert.Same(pane, pane.Session.ActivePane.Value));
        }
        Assert.Equal(reads, fixture.Commands.Count);
        Assert.Null(fixture.Server.SnapshotMetadata);
    }

    [Theory]
    [InlineData(SnapshotDepth.Server, "server")]
    [InlineData(SnapshotDepth.Sessions, "list-sessions")]
    [InlineData(SnapshotDepth.Windows, "list-windows")]
    [InlineData(SnapshotDepth.Panes, "list-panes")]
    public async Task Cancellation_after_the_last_reply_returns_no_snapshot(SnapshotDepth depth, string last)
    {
        var fixture = new AcquisitionFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Replied = command =>
        {
            if (command == last)
            {
                cancellation.Cancel();
            }
        };

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Server.CaptureSnapshotAsync(depth, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Null(fixture.Server.SnapshotMetadata);
    }

    [Fact]
    public async Task Recapture_preserves_the_original_graph_and_metadata()
    {
        var fixture = new AcquisitionFixture();
        Server first = await fixture.Server.CaptureSnapshotAsync(SnapshotDepth.Panes, TestContext.Current.CancellationToken);
        SnapshotMetadata metadata = Assert.IsType<SnapshotMetadata>(first.SnapshotMetadata);
        Server second = await first.CaptureSnapshotAsync(SnapshotDepth.Sessions, TestContext.Current.CancellationToken);
        Assert.NotSame(first, second);
        Assert.NotSame(metadata, second.SnapshotMetadata);
        Assert.Equal(SnapshotDepth.Sessions, second.SnapshotMetadata!.Depth);
        Assert.False(second.Windows.IsCaptured);
        fixture.Failure = new OperationCanceledException(TestContext.Current.CancellationToken);
        Exception? failure = await Record.ExceptionAsync(() => first.CaptureSnapshotAsync(SnapshotDepth.Panes, TestContext.Current.CancellationToken));
        Assert.Same(fixture.Failure, failure);
        Assert.Same(metadata, first.SnapshotMetadata);
        Assert.Single(first.Sessions);
        Assert.Single(first.Windows);
        Assert.Single(first.Panes);
    }

    [Fact]
    public async Task Cancellation_before_publication_does_not_return_the_assembled_graph()
    {
        var fixture = new AcquisitionFixture();
        using var cancellation = new CancellationTokenSource();
        var clock = new AcquisitionClock { Completing = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Server.CaptureSnapshotAsync(
            SnapshotDepth.Panes, clock, cancellation.Token));

        Assert.Equal(3, fixture.Commands.Count);
        Assert.Null(fixture.Server.SnapshotMetadata);
    }

    [Fact]
    public async Task Acquisition_interval_includes_discovery_and_initialization()
    {
        var clock = new AcquisitionClock();
        DateTimeOffset started = clock.Utc;
        var fixture = new AcquisitionFixture(connected: false, initialize: clock.Advance)
        {
            Replied = _ => clock.Advance(),
        };

        Server captured = await fixture.Server.CaptureSnapshotAsync(SnapshotDepth.Server, clock, TestContext.Current.CancellationToken);

        SnapshotMetadata metadata = Assert.IsType<SnapshotMetadata>(captured.SnapshotMetadata);
        Assert.Equal(started, metadata.StartedAtUtc);
        Assert.Equal(started.AddHours(-3), metadata.CompletedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(3), metadata.Elapsed);
        Assert.Equal(2, fixture.Commands.Count);
    }

    [Theory]
    [InlineData(SnapshotDepth.Server)]
    [InlineData(SnapshotDepth.Sessions)]
    [InlineData(SnapshotDepth.Windows)]
    [InlineData(SnapshotDepth.Panes)]
    public async Task Transport_cancellation_keeps_its_identity(SnapshotDepth depth)
    {
        var fixture = new AcquisitionFixture
        {
            Failure = new TmuxOperationCanceledException("Transport cancelled.", TestContext.Current.CancellationToken, true, 12),
        };

        Exception? failure = await Record.ExceptionAsync(() => fixture.Server.CaptureSnapshotAsync(depth, TestContext.Current.CancellationToken));

        Assert.Same(fixture.Failure, failure);
    }

    [Theory]
    [InlineData("list-sessions", "window_index", "9", SnapshotDepth.Windows)]
    [InlineData("list-sessions", "pane_id", "%9", SnapshotDepth.Panes)]
    [InlineData("list-windows", "pane_id", "%9", SnapshotDepth.Panes)]
    public async Task Active_references_must_belong_to_the_acquired_placement(
        string list, string field, string value, SnapshotDepth depth)
    {
        var fixture = new AcquisitionFixture
        {
            OverrideField = (command, wireName) => command == list && wireName == field ? value : null,
        };

        InconsistentSnapshotException failure = await Assert.ThrowsAsync<InconsistentSnapshotException>(
            () => fixture.Server.CaptureSnapshotAsync(depth, TestContext.Current.CancellationToken));

        Assert.Equal(depth, failure.RequestedDepth);
        Assert.Equal(Generation, failure.Generation);
        Assert.Equal(TmuxDispatchState.Dispatched, failure.Dispatch);
        Assert.Null(fixture.Server.SnapshotMetadata);
    }

    private sealed class AcquisitionClock : TimeProvider
    {
        private long _timestamp;
        private int _utcReads;

        internal DateTimeOffset Utc { get; private set; } = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        internal Action? Completing { get; init; }

        public override DateTimeOffset GetUtcNow()
        {
            if (++_utcReads == 2)
            {
                Completing?.Invoke();
            }
            return Utc;
        }

        public override long GetTimestamp() => _timestamp;

        internal void Advance()
        {
            Utc = Utc.AddHours(-1);
            _timestamp += TimeSpan.TicksPerSecond;
        }
    }

    private sealed class AcquisitionFixture
    {
        internal AcquisitionFixture(bool connected = true, Action? initialize = null)
        {
            var connection = new TmuxConnection(new ServerConnectionOptions
            {
                SocketName = "snapshot-unit",
                InitializeAsync = (_, _) =>
                {
                    initialize?.Invoke();
                    return ValueTask.CompletedTask;
                }
            }, ExecuteAsync);
            Server = new Server(connection, connected ? Generation : null, connected ? "tmux 3.7c" : null);
        }

        internal Server Server { get; }

        internal List<string> Commands { get; } = [];

        internal Action<string>? Replied { get; set; }

        internal bool RefuseDispatch { get; set; }

        internal Exception? Failure { get; set; }

        internal Func<string, string, string?>? OverrideField { get; init; }

        private Task<TmuxCommandResult> ExecuteAsync(TmuxCommandRequest request, CancellationToken cancellationToken)
        {
            if (RefuseDispatch)
            {
                throw new InvalidOperationException("Reading a snapshot reached tmux.");
            }
            if (request.LogicalArguments.Contains("-V", StringComparer.Ordinal))
            {
                return Task.FromResult(Result(request.LogicalArguments, "tmux 3.7c\n"));
            }
            if (Failure is Exception failure)
            {
                return Task.FromException<TmuxCommandResult>(failure);
            }
            string command = request.LogicalArguments.FirstOrDefault(argument => argument.StartsWith("list-", StringComparison.Ordinal)) ?? "server";
            Commands.Add(command);
            string payload = command == "server"
                ? "91:901\n"
                : string.Concat(FormatProjection.Create(command, TmuxVersion.Parse("3.7c")).Fields.Select(
                    field => (OverrideField?.Invoke(command, field.WireName) ?? Value(field.WireName))
                        + FormatProjection.RowSeparator)) + "\n";
            string prefix = request.LogicalArguments.Contains("if-shell", StringComparer.Ordinal) ? "91:901\n" : string.Empty;
            TmuxCommandResult result = Result(request.LogicalArguments, prefix + payload);
            Replied?.Invoke(command);
            return Task.FromResult(result);
        }

        private static string Value(string field) => field switch
        {
            "pid" => "91",
            "start_time" => "901",
            "session_id" => "$0",
            "session_windows" => "1",
            "window_id" => "@0",
            "window_index" => "5",
            "window_panes" => "1",
            "pane_id" => "%0",
            "pane_index" => "2",
            _ => string.Empty,
        };

        private static TmuxCommandResult Result(IReadOnlyList<string> arguments, string output)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(output);
            return new TmuxCommandResult(arguments, 0, bytes, ReadOnlyMemory<byte>.Empty, Utf8BackslashDecoder.ProjectOutputLines(bytes), []);
        }
    }

    private static Server CreateServer(Action onDispatch)
    {
        var connection = new TmuxConnection(
            new ServerConnectionOptions
            {
                SocketName = "snapshot-unit"
            },
            (_, _) =>
            {
                onDispatch();
                throw new InvalidOperationException("Unexpected snapshot dispatch.");
            });
        return new Server(connection, new ServerGeneration(91, 901), "tmux 3.7c");
    }
}
