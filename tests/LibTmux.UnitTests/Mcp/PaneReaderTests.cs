using System.Runtime.Versioning;
using LibTmux.Internal;

using LibTmux.UnitTests.Connection;

namespace LibTmux.UnitTests.Mcp;

[UnsupportedOSPlatform("windows")]
public sealed class PaneReaderTests
{
    [Fact]
    public void A_redrawn_row_below_the_cursor_does_not_replay_the_rows_beside_it()
    {
        PaneCursor cursor = CursorOver(["prompt$ ", "one", "two", "three"]);

        List<string> reported = PaneReader.DropAlreadySeen(
            ["prompt$ ", "one", "two", "redrawn"],
            cursor);

        Assert.Equal(["redrawn"], reported);
    }

    [Fact]
    public void A_rewritten_row_between_unchanged_rows_is_the_only_one_reported()
    {
        PaneCursor cursor = CursorOver(["prompt$ ", "one", "two", "three"]);

        List<string> reported = PaneReader.DropAlreadySeen(
            ["prompt$ ", "one", "rewritten", "three"],
            cursor);

        Assert.Equal(["rewritten"], reported);
    }

    [Fact]
    public void An_unchanged_screen_reports_nothing()
    {
        PaneCursor cursor = CursorOver(["prompt$ ", "one", "two", "three"]);

        List<string> reported = PaneReader.DropAlreadySeen(
            ["prompt$ ", "one", "two", "three"],
            cursor);

        Assert.Empty(reported);
    }

    [Fact]
    public void Rows_written_past_the_previous_screen_are_reported_in_order()
    {
        PaneCursor cursor = CursorOver(["prompt$ ", "one", "two"]);

        List<string> reported = PaneReader.DropAlreadySeen(
            ["prompt$ ", "one", "two", "three", "four"],
            cursor);

        Assert.Equal(["three", "four"], reported);
    }

    [Fact]
    public void A_rewritten_anchor_row_is_reported_with_the_rows_that_changed()
    {
        PaneCursor cursor = CursorOver(["prompt$ ", "one", "two"]);

        List<string> reported = PaneReader.DropAlreadySeen(
            ["prompt$ typed", "one", "changed"],
            cursor);

        Assert.Equal(["prompt$ typed", "changed"], reported);
    }

    [Fact]
    public void Rows_past_the_tracked_window_are_reported_rather_than_guessed()
    {
        string[] before = ["prompt$ ", .. Enumerable.Range(0, 40).Select(index => $"row {index}")];
        string[] after = [.. before];
        after[^1] = "row 39 redrawn";
        PaneCursor cursor = CursorOver(before);

        List<string> reported = PaneReader.DropAlreadySeen(after, cursor);

        Assert.Equal(before[33..^1].Append("row 39 redrawn"), reported);
    }

    private static PaneCursor CursorOver(IReadOnlyList<string> cursorRows) => PaneCursor.Build(
        Pane(),
        new PaneGridState("313", 2, 1_000, 64, 1, false, false),
        cursorRows);

    [Fact]
    public async Task A_control_client_that_never_answers_gives_way_to_a_process_within_the_command_timeout()
    {
        var connection = new TmuxConnection(
            new ServerConnectionOptions { SocketName = "pane-reader", CommandTimeout = TimeSpan.FromMilliseconds(100) },
            FakeMultiplexer.AnsweringVersion(static (request, _) =>
            {
                // A command reaching tmux carries the server generation's guard first.
                string body = request.LogicalArguments.Contains("capture-pane")
                    ? "first\nsecond\n"
                    : "313\t2\t1000\t24\t1\t0\t0\t80\n";
                if (request.LogicalArguments.Contains("#{pid}:#{start_time}"))
                {
                    body = "17:9001\n" + body;
                }

                byte[] output = System.Text.Encoding.UTF8.GetBytes(body);
                return Task.FromResult(new TmuxCommandResult(
                    request.LogicalArguments,
                    0,
                    output,
                    ReadOnlyMemory<byte>.Empty,
                    Utf8BackslashDecoder.ProjectOutputLines(output),
                    []));
            }));
        var server = new Server(connection, new ServerGeneration(17, 9001), "tmux 3.7");
        var pane = new Pane(
            server,
            connection,
            new ServerGeneration(17, 9001),
            new PaneId(1),
            new Dictionary<string, string?>(StringComparer.Ordinal));

        PaneRead read = await PaneReader
            .ReadVisibleAsync(pane, null, PaneReader.Failure, new SilentControlSession(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], read.Lines);
    }

    private static Pane Pane()
    {
        var connection = new TmuxConnection(
            new ServerConnectionOptions { SocketName = "pane-reader" },
            FakeMultiplexer.AnsweringVersion(static (request, _) => Task.FromResult(new TmuxCommandResult(
                request.LogicalArguments,
                0,
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty,
                [],
                []))));
        var server = new Server(connection, new ServerGeneration(17, 9001), "tmux 3.7");
        return new Pane(
            server,
            connection,
            new ServerGeneration(17, 9001),
            new PaneId(1),
            new Dictionary<string, string?>(StringComparer.Ordinal));
    }

    private sealed class SilentControlSession : IControlModeSession
    {
        public IAsyncEnumerable<TmuxEvent> Events => System.Threading.Channels.Channel.CreateUnbounded<TmuxEvent>().Reader.ReadAllAsync();

        public bool IsRunning => true;

        public async Task<IReadOnlyList<string>> SendAsync(
            TmuxCommand command,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
