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
    public async Task Line_reader_rejects_a_line_beyond_its_byte_limit()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var input = new MemoryStream("123456\n"u8.ToArray());
        var reader = new ControlModeLineReader(input, maxLineBytes: 5, bufferSize: 2);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
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
