using System.Buffers;
using System.Text;

namespace LibTmux;

internal sealed class ControlModeLineReader
{
    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly byte[] _buffer;
    private readonly int _maxLineBytes;
    private readonly Stream _stream;
    private int _end;
    private int _start;

    internal ControlModeLineReader(
        Stream stream,
        int maxLineBytes,
        int bufferSize = 4096)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        _maxLineBytes = maxLineBytes;
        _buffer = new byte[Math.Min(bufferSize, maxLineBytes)];
    }

    internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        ArrayBufferWriter<byte>? line = null;
        while (true)
        {
            int available = _end - _start;
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start, available);
            if (newline >= 0)
            {
                int finalBytes = newline - _start;
                EnsureWithinLimit((line?.WrittenCount ?? 0) + finalBytes);
                string result = Decode(line, _buffer, _start, finalBytes);
                _start = newline + 1;
                return result;
            }

            if (available > 0)
            {
                line ??= new ArrayBufferWriter<byte>(Math.Min(_maxLineBytes, _buffer.Length));
                EnsureWithinLimit(line.WrittenCount + available);
                Append(line, _buffer, _start, available);
                _start = _end;
            }

            _start = 0;
            _end = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            if (_end != 0)
            {
                continue;
            }

            return line is null ? null : Decode(line, [], 0, 0);
        }
    }

    private static void Append(
        ArrayBufferWriter<byte> destination,
        byte[] source,
        int start,
        int length) =>
        destination.Write(source.AsSpan(start, length));

    private static string Decode(
        ArrayBufferWriter<byte>? prefix,
        byte[] final,
        int start,
        int length)
    {
        if (prefix is null)
        {
            ReadOnlySpan<byte> bytes = final.AsSpan(start, length);
            return Decode(bytes.EndsWith("\r"u8) ? bytes[..^1] : bytes);
        }

        prefix.Write(final.AsSpan(start, length));
        ReadOnlySpan<byte> completed = prefix.WrittenSpan;
        return Decode(completed.EndsWith("\r"u8) ? completed[..^1] : completed);
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException(
                "The tmux control client sent invalid UTF-8.",
                error);
        }
    }

    private void EnsureWithinLimit(int bytes)
    {
        if (bytes > _maxLineBytes)
        {
            throw new InvalidDataException(
                $"A tmux control-mode line exceeded {_maxLineBytes} bytes.");
        }
    }
}
