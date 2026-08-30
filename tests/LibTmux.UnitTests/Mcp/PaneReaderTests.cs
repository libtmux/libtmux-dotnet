using System.Runtime.Versioning;
using LibTmux.Internal;
using LibTmux.Mcp;

using LibTmux.UnitTests.Connection;

namespace LibTmux.UnitTests.Mcp;

[UnsupportedOSPlatform("windows")]
public sealed class PaneReaderTests
{
    [Fact]
    public void A_redrawn_row_below_the_cursor_does_not_replay_the_rows_beside_it()
    {
        TailCursor cursor = CursorOver(["prompt$ ", "one", "two", "three"]);

        List<string> reported = PaneReader.DropAlreadySeen(
            ["prompt$ ", "one", "two", "redrawn"],
            cursor);

        Assert.Equal(["redrawn"], reported);
    }

    [Fact]
    public void A_rewritten_row_between_unchanged_rows_is_the_only_one_reported()
    {
        TailCursor cursor = CursorOver(["prompt$ ", "one", "two", "three"]);

        List<string> reported = PaneReader.DropAlreadySeen(
            ["prompt$ ", "one", "rewritten", "three"],
            cursor);

        Assert.Equal(["rewritten"], reported);
    }

    [Fact]
    public void An_unchanged_screen_reports_nothing()
    {
        TailCursor cursor = CursorOver(["prompt$ ", "one", "two", "three"]);

        List<string> reported = PaneReader.DropAlreadySeen(
            ["prompt$ ", "one", "two", "three"],
            cursor);

        Assert.Empty(reported);
    }

    [Fact]
    public void Rows_written_past_the_previous_screen_are_reported_in_order()
    {
        TailCursor cursor = CursorOver(["prompt$ ", "one", "two"]);

        List<string> reported = PaneReader.DropAlreadySeen(
            ["prompt$ ", "one", "two", "three", "four"],
            cursor);

        Assert.Equal(["three", "four"], reported);
    }

    [Fact]
    public void A_rewritten_anchor_row_is_reported_with_the_rows_that_changed()
    {
        TailCursor cursor = CursorOver(["prompt$ ", "one", "two"]);

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
        TailCursor cursor = CursorOver(before);

        List<string> reported = PaneReader.DropAlreadySeen(after, cursor);

        Assert.Equal(before[33..^1].Append("row 39 redrawn"), reported);
    }

    private static TailCursor CursorOver(IReadOnlyList<string> cursorRows) => TailCursor.Build(
        Pane(),
        new PaneGridState("313", 2, 1_000, 64, 1, false, false),
        cursorRows);

    private static Pane Pane()
    {
        var connection = new TmuxConnection(
            new ServerConnectionOptions(socketName: "pane-reader"),
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
}
