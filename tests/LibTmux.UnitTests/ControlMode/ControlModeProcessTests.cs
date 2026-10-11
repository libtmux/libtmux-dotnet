using System.Text;

namespace LibTmux.UnitTests.ControlMode;

public sealed class ControlModeProcessTests
{
    [Fact]
    public async Task Line_reader_handles_split_utf8_crlf_and_final_lines()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("alpha\r\nπ\nlast"));
        var reader = new ControlModeLineReader(input, maxLineBytes: 16, bufferSize: 2);

        Assert.Equal("alpha", await reader.ReadLineAsync(token));
        Assert.Equal("π", await reader.ReadLineAsync(token));
        Assert.Equal("last", await reader.ReadLineAsync(token));
        Assert.Null(await reader.ReadLineAsync(token));
    }

    [Fact]
    public async Task A_line_over_the_limit_is_dropped_whole_and_the_next_line_is_read()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("aaaaaaaaaaaaaaaa\nok\nbbbbbbbbbb"));
        var reader = new ControlModeLineReader(input, maxLineBytes: 5, bufferSize: 2, resynchronize: true);

        await Assert.ThrowsAsync<ControlModeOversizedLineException>(() => reader.ReadLineAsync(token));

        Assert.Equal("ok", await reader.ReadLineAsync(token));
        await Assert.ThrowsAsync<ControlModeOversizedLineException>(() => reader.ReadLineAsync(token));
        Assert.Null(await reader.ReadLineAsync(token));
    }

    // tmux escapes only control bytes and backslash in control-mode output, so
    // a pane that prints invalid UTF-8 sends those bytes raw.
    [Fact]
    public async Task Line_reader_projects_invalid_utf8_as_the_one_shot_path_does()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var input = new MemoryStream([0x61, 0xFF, 0x62, 0x0A, 0xCF, 0x80, 0x0A]);
        var reader = new ControlModeLineReader(input, maxLineBytes: 16, bufferSize: 2);

        Assert.Equal("a\\xffb", await reader.ReadLineAsync(token));
        Assert.Equal("π", await reader.ReadLineAsync(token));
    }

    [Fact]
    public async Task Line_reader_rejects_a_line_beyond_its_byte_limit()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var input = new MemoryStream("123456\n"u8.ToArray());
        var reader = new ControlModeLineReader(input, maxLineBytes: 5, bufferSize: 2);

        TmuxProtocolException error = await Assert.ThrowsAsync<TmuxProtocolException>(
            () => reader.ReadLineAsync(token));

        Assert.Equal("A tmux control-mode line exceeded 5 bytes.", error.Message);
    }

    [Fact]
    public void Standard_error_tail_keeps_only_the_newest_bytes()
    {
        var tail = new RollingByteTail(capacity: 5);

        tail.Append("abc"u8);
        tail.Append("defg"u8);

        Assert.Equal("cdefg"u8.ToArray(), tail.Snapshot());
    }
}
