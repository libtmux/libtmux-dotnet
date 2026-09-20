using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Materialization;

public sealed class SeparatedRowFramerTests
{
    private static readonly FormatProjection Projection =
        FormatProjection.Create("list-sessions", TmuxVersion.Parse("3.7b"));
    private static readonly TmuxTransportLimits Limits = new();
    private static readonly byte[] Separator =
        Encoding.ASCII.GetBytes(FormatProjection.RowSeparator);

    [Fact]
    public void Round_trips_a_row_whose_values_contain_newlines_and_delimiters()
    {
        // Every byte a value could hold, including the row terminator and the
        // punctuation the previous byte-count protocol reserved.
        byte[] hostile = Encoding.UTF8.GetBytes("a\nb:9:c\r\nd");
        byte[] payload = Frame(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["session_name"] = hostile,
        });

        IReadOnlyList<IReadOnlyDictionary<string, ReadOnlyMemory<byte>?>> rows =
            SeparatedRowFramer.Decode(payload, Projection, Limits);

        Assert.Single(rows);
        Assert.Equal(hostile, rows[0]["session_name"]!.Value.ToArray());
    }

    [Fact]
    public void Preserves_invalid_utf8_bytes_without_decoding()
    {
        byte[] invalid = [0x61, 0xC3, 0x28, 0xFF, 0x62];
        byte[] payload = Frame(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["session_name"] = invalid,
        });

        IReadOnlyList<IReadOnlyDictionary<string, ReadOnlyMemory<byte>?>> rows =
            SeparatedRowFramer.Decode(payload, Projection, Limits);

        Assert.Equal(invalid, rows[0]["session_name"]!.Value.ToArray());
    }

    [Fact]
    public void Empty_values_decode_as_null_with_their_key_present()
    {
        byte[] payload = Frame([]);

        IReadOnlyList<IReadOnlyDictionary<string, ReadOnlyMemory<byte>?>> rows =
            SeparatedRowFramer.Decode(payload, Projection, Limits);

        Assert.True(rows[0].ContainsKey("session_name"));
        Assert.Null(rows[0]["session_name"]);
        Assert.Equal(Projection.Fields.Count, rows[0].Count);
    }

    [Fact]
    public void Fields_are_read_positionally_because_names_never_reach_the_wire()
    {
        byte[] payload = Frame(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["session_name"] = "named"u8.ToArray(),
        });

        // The template sends values only; both ends derive the same field order
        // from the same list command and tmux version.
        Assert.DoesNotContain(
            "session_name",
            Encoding.ASCII.GetString(payload),
            StringComparison.Ordinal);

        IReadOnlyList<IReadOnlyDictionary<string, ReadOnlyMemory<byte>?>> rows =
            SeparatedRowFramer.Decode(payload, Projection, Limits);

        Assert.Equal("named"u8.ToArray(), rows[0]["session_name"]!.Value.ToArray());
    }

    [Fact]
    public void Decodes_multiple_rows_including_one_ending_at_end_of_input()
    {
        byte[] first = Frame(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["session_name"] = "one"u8.ToArray(),
        });
        byte[] second = FrameWithoutTerminator(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["session_name"] = "two"u8.ToArray(),
            });

        IReadOnlyList<IReadOnlyDictionary<string, ReadOnlyMemory<byte>?>> rows =
            SeparatedRowFramer.Decode([.. first, .. second], Projection, Limits);

        Assert.Equal(2, rows.Count);
        Assert.Equal("one"u8.ToArray(), rows[0]["session_name"]!.Value.ToArray());
        Assert.Equal("two"u8.ToArray(), rows[1]["session_name"]!.Value.ToArray());
    }

    [Fact]
    public void Accepts_carriage_return_line_feed_row_separators()
    {
        byte[] row = FrameWithoutTerminator(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["session_name"] = "one"u8.ToArray(),
            });

        IReadOnlyList<IReadOnlyDictionary<string, ReadOnlyMemory<byte>?>> rows =
            SeparatedRowFramer.Decode([.. row, .. "\r\n"u8], Projection, Limits);

        Assert.Single(rows);
    }

    [Fact]
    public void Returned_memories_do_not_alias_the_source_buffer()
    {
        byte[] payload = Frame(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["session_name"] = "keep"u8.ToArray(),
        });
        IReadOnlyList<IReadOnlyDictionary<string, ReadOnlyMemory<byte>?>> rows =
            SeparatedRowFramer.Decode(payload, Projection, Limits);
        Array.Fill(payload, (byte)'X');

        Assert.Equal("keep"u8.ToArray(), rows[0]["session_name"]!.Value.ToArray());
    }

    [Fact]
    public void Rejects_a_row_that_ends_before_every_field_is_read()
    {
        byte[] payload = [.. "one"u8, .. Separator, .. "two"u8, .. Separator, .. "\n"u8];

        // tmux answered; the rows it sent could not be read. A retry repeats
        // whatever the command already did, so the failure says Dispatched.
        TmuxProtocolException error = Assert.Throws<TmuxProtocolException>(
            () => SeparatedRowFramer.Decode(payload, Projection, Limits));
        Assert.Equal(TmuxDispatchState.Dispatched, error.Dispatch);
    }

    [Fact]
    public void Rejects_a_final_value_that_never_closes()
    {
        byte[] payload = [.. "unterminated"u8];

        // tmux answered; the rows it sent could not be read. A retry repeats
        // whatever the command already did, so the failure says Dispatched.
        TmuxProtocolException error = Assert.Throws<TmuxProtocolException>(
            () => SeparatedRowFramer.Decode(payload, Projection, Limits));
        Assert.Equal(TmuxDispatchState.Dispatched, error.Dispatch);
    }

    [Fact]
    public void Rejects_a_row_that_is_not_terminated_by_a_newline()
    {
        byte[] row = FrameWithoutTerminator(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["session_name"] = "one"u8.ToArray(),
            });

        TmuxProtocolException error = Assert.Throws<TmuxProtocolException>(
            () => SeparatedRowFramer.Decode([.. row, .. "junk"u8], Projection, Limits));
        Assert.Equal(TmuxDispatchState.Dispatched, error.Dispatch);
    }

    [Fact]
    public void Rejects_a_value_larger_than_the_framed_field_limit()
    {
        var bounded = new TmuxTransportLimits(MaxFramedFieldBytesValue: 8);
        byte[] payload = Frame(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["session_name"] = Encoding.ASCII.GetBytes(new string('x', 64)),
        });

        TmuxProtocolException error = Assert.Throws<TmuxProtocolException>(
            () => SeparatedRowFramer.Decode(payload, Projection, bounded));
        Assert.Equal(TmuxDispatchState.Dispatched, error.Dispatch);
    }

    [Fact]
    public void Decodes_nothing_from_an_empty_payload() =>
        Assert.Empty(SeparatedRowFramer.Decode([], Projection, Limits));

    [Fact]
    public void Query_markers_are_private_row_aligned_and_strictly_boolean()
    {
        FormatProjection marked = FormatProjection.Create("list-sessions", TmuxVersion.Parse("3.7b"), "1");
        Assert.Equal(Projection.Fields, marked.Fields);
        Assert.Equal(Projection.FramedFieldCount + 1, marked.FramedFieldCount);
        byte[] row = FrameWithoutTerminator([]);
        byte[] payload = [.. row, .. "1"u8, .. Separator, .. "\n"u8,
            .. row, .. "0"u8, .. Separator, .. "\n"u8];
        var rows = SeparatedRowFramer.Decode(payload, marked, Limits, out IReadOnlyList<bool>? matches);
        Assert.Equal([true, false], matches);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, values => Assert.Equal(Projection.Fields.Count, values.Count));
        Assert.Throws<TmuxProtocolException>(() => SeparatedRowFramer.Decode(
            [.. row, .. "2"u8, .. Separator, .. "\n"u8], marked, Limits, out _));
        Assert.Throws<TmuxProtocolException>(() => SeparatedRowFramer.Decode(
            [.. row, .. "\n"u8], marked, Limits, out _));
        Assert.Empty(SeparatedRowFramer.Decode([], marked, Limits, out matches));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<bool>>(matches));
    }

    private static byte[] Frame(Dictionary<string, byte[]> values) =>
        [.. FrameWithoutTerminator(values), .. "\n"u8];

    private static byte[] FrameWithoutTerminator(Dictionary<string, byte[]> values) =>
    [
        .. Projection.Fields.SelectMany(field =>
            values.TryGetValue(field.WireName, out byte[]? value)
                ? value.Concat(Separator)
                : Separator),
    ];
}
