using System.Buffers;
using LibTmux.Internal;

namespace LibTmux;

internal sealed class ControlModeLineReader
{
    private readonly byte[] _buffer;
    private readonly int _maxLineBytes;
    private readonly bool _resynchronize;
    private readonly Stream _stream;
    private int _end;
    private int _start;

    internal ControlModeLineReader(
        Stream stream,
        int maxLineBytes,
        int bufferSize = 4096,
        bool resynchronize = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        _maxLineBytes = maxLineBytes;
        _resynchronize = resynchronize;
        _buffer = new byte[Math.Min(bufferSize, maxLineBytes)];
    }

    internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        ArrayBufferWriter<byte>? line = null;
        bool oversized = false;
        while (true)
        {
            int available = _end - _start;
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start, available);
            if (newline >= 0)
            {
                int finalBytes = newline - _start;
                if (oversized || (line?.WrittenCount ?? 0) + finalBytes > _maxLineBytes)
                {
                    // The rest of the line is read and dropped, so the next
                    // line starts where the stream says it does.
                    _start = newline + 1;
                    throw Oversized();
                }

                string result = Decode(line, _buffer, _start, finalBytes);
                _start = newline + 1;
                return result;
            }

            if (available > 0)
            {
                line ??= new ArrayBufferWriter<byte>(Math.Min(_maxLineBytes, _buffer.Length));
                if (oversized || line.WrittenCount + available > _maxLineBytes)
                {
                    oversized = true;
                    if (!_resynchronize)
                    {
                        throw Oversized();
                    }
                }
                else
                {
                    Append(line, _buffer, _start, available);
                }

                _start = _end;
            }

            _start = 0;
            _end = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            if (_end != 0)
            {
                continue;
            }

            if (oversized)
            {
                throw Oversized();
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

    // tmux escapes only control bytes and backslash, so pane output reaches
    // here raw and may not be UTF-8. Project it the way one-shot output is.
    private static string Decode(ReadOnlySpan<byte> bytes) =>
        Utf8BackslashDecoder.ProjectValue(bytes);

    private Exception Oversized() => _resynchronize
        ? new ControlModeOversizedLineException(_maxLineBytes)
        : new TmuxProtocolException(
            $"A tmux control-mode line exceeded {_maxLineBytes} bytes.",
            TmuxDispatchState.Unknown);
}

/// <summary>A line longer than the reader allows, read to its end and dropped.</summary>
internal sealed class ControlModeOversizedLineException(int limit)
    : Exception($"A tmux control-mode line exceeded {limit} bytes.");
