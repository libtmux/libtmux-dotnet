using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;
using LibTmux.Mcp;
using LibTmux.UnitTests.Connection;
using ModelContextProtocol;

namespace LibTmux.UnitTests;

[UnsupportedOSPlatform("windows")]
public sealed class TailCursorTests
{
    [Fact]
    public void A_cursor_round_trips_for_its_exact_endpoint_generation_and_pane()
    {
        Pane pane = PaneFor("cursor-one", new ServerGeneration(17, 9001), 3);
        TailCursor cursor = CursorFor(pane);

        TailCursor? decoded = TailCursor.Decode(cursor.Encode(), pane);

        Assert.Equal(cursor, decoded);
    }

    [Fact]
    public void A_cursor_stays_bounded_instead_of_copying_every_row_hash()
    {
        Pane pane = PaneFor("cursor-size", new ServerGeneration(int.MaxValue, long.MaxValue), 99_999);
        var state = new PaneGridState(
            int.MaxValue.ToString(CultureInfo.InvariantCulture),
            2,
            50_000,
            20_000,
            1,
            false,
            false);
        string wideRow = string.Concat(Enumerable.Repeat("\U0001f642", 80));
        string[] rows = Enumerable.Repeat(wideRow, 10_000).ToArray();

        string token = TailCursor.Build(pane, state, rows).Encode();
        TailCursor decoded = Assert.IsType<TailCursor>(TailCursor.Decode(token, pane));

        // Decode enforces these ceilings, so a cursor that cannot be read back
        // would be issued by every tail of a tall pane.
        Assert.InRange(token.Length, 1, 2_048);
        Assert.Equal(32, decoded.BelowCount);
        Assert.Equal(9_999, decoded.SuffixCount);
        Assert.Equal(32 * 8, Convert.FromBase64String(
            decoded.RowHashes!.Replace('-', '+').Replace('_', '/') + "==").Length);
    }

    [Fact]
    public void A_cursor_with_nothing_below_it_round_trips()
    {
        Pane pane = PaneFor("cursor-bottom", new ServerGeneration(17, 9001), 3);
        TailCursor cursor = TailCursor.Build(
            pane,
            new PaneGridState("313", 23, 1_000, 24, 1, false, false),
            ["only the cursor row"]);

        TailCursor decoded = Assert.IsType<TailCursor>(TailCursor.Decode(cursor.Encode(), pane));

        Assert.Null(decoded.RowHashes);
        Assert.Equal(0, decoded.BelowCount);
        Assert.Equal(cursor, decoded);
    }

    [Fact]
    public void A_modified_token_is_rejected()
    {
        Pane pane = PaneFor("cursor-tamper", new ServerGeneration(17, 9001), 3);
        string token = CursorFor(pane).Encode();
        char replacement = token[^1] == 'a' ? 'b' : 'a';

        McpException error = Assert.Throws<McpException>(() =>
            TailCursor.Decode(token[..^1] + replacement, pane));

        Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_cursor_cannot_cross_endpoints()
    {
        var generation = new ServerGeneration(17, 9001);
        Pane issuedFor = PaneFor("cursor-endpoint-a", generation, 3);
        Pane presentedTo = PaneFor("cursor-endpoint-b", generation, 3);

        McpException error = Assert.Throws<McpException>(() =>
            TailCursor.Decode(CursorFor(issuedFor).Encode(), presentedTo));

        Assert.Contains("different pane or tmux server", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cursor_cannot_cross_server_generations()
    {
        Pane issuedFor = PaneFor("cursor-generation", new ServerGeneration(17, 9001), 3);
        Pane presentedTo = PaneFor("cursor-generation", new ServerGeneration(17, 9002), 3);

        Assert.Throws<McpException>(() =>
            TailCursor.Decode(CursorFor(issuedFor).Encode(), presentedTo));
    }

    [Fact]
    public void A_cursor_cannot_cross_panes()
    {
        var generation = new ServerGeneration(17, 9001);
        Pane issuedFor = PaneFor("cursor-pane", generation, 3);
        Pane presentedTo = PaneFor("cursor-pane", generation, 4);

        Assert.Throws<McpException>(() =>
            TailCursor.Decode(CursorFor(issuedFor).Encode(), presentedTo));
    }

    [Fact]
    public void An_authenticated_cursor_with_a_null_required_field_is_rejected_cleanly()
    {
        Pane pane = PaneFor("cursor-null", new ServerGeneration(17, 9001), 3);
        TailCursor malformed = CursorFor(pane) with { EndpointFingerprint = null! };

        McpException error = Assert.Throws<McpException>(() =>
            TailCursor.Decode(malformed.Encode(), pane));

        Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_authenticated_cursor_with_an_unknown_version_is_rejected()
    {
        Pane pane = PaneFor("cursor-version", new ServerGeneration(17, 9001), 3);
        TailCursor malformed = CursorFor(pane) with { Version = int.MaxValue };

        Assert.Throws<McpException>(() => TailCursor.Decode(malformed.Encode(), pane));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\ttmux-tail-v3:")]
    public void An_explicit_blank_or_padded_cursor_is_rejected(string token)
    {
        Pane pane = PaneFor("cursor-whitespace", new ServerGeneration(17, 9001), 3);

        Assert.Throws<McpException>(() => TailCursor.Decode(token, pane));
    }

    private static TailCursor CursorFor(Pane pane) => TailCursor.Build(
        pane,
        new PaneGridState("313", 2, 1_000, 24, 1, false, false),
        ["anchor", "below"]);

    private static Pane PaneFor(string socketName, ServerGeneration generation, int paneId)
    {
        var connection = new TmuxConnection(
            new ServerConnectionOptions(socketName: socketName),
            FakeMultiplexer.AnsweringVersion(static (request, _) => Task.FromResult(new TmuxCommandResult(
                request.LogicalArguments,
                0,
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty,
                [],
                []))));
        var server = new Server(connection, generation, "tmux 3.7");
        return new Pane(
            server,
            connection,
            generation,
            new PaneId(paneId),
            new Dictionary<string, string?>(StringComparer.Ordinal));
    }
}
