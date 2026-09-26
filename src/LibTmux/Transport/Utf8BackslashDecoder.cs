using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace LibTmux.Internal;

internal static class Utf8BackslashDecoder
{
    internal static IReadOnlyList<string> ProjectOutputLines(ReadOnlySpan<byte> bytes) =>
        ProjectLines(bytes, removeAllEmptyLines: false);

    internal static IReadOnlyList<string> ProjectErrorLines(ReadOnlySpan<byte> bytes) =>
        ProjectLines(bytes, removeAllEmptyLines: true);

    /// <summary>Projects one raw tmux value without splitting it into lines.</summary>
    /// <remarks>
    /// A framed value owns its newlines, so line projection would corrupt it.
    /// </remarks>
    internal static string ProjectValue(ReadOnlySpan<byte> bytes) => Decode(bytes);

    private static string[] ProjectLines(
        ReadOnlySpan<byte> bytes,
        bool removeAllEmptyLines)
    {
        string normalized = Decode(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        if (removeAllEmptyLines)
        {
            return [.. lines.Where(static line => line.Length > 0)];
        }

        int count = lines.Length;
        while (count > 0 && lines[count - 1].Length == 0)
        {
            count--;
        }

        return lines[..count];
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        // Nearly every line is valid UTF-8, and one vectorized pass decodes it.
        if (Utf8.IsValid(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        var decoded = new StringBuilder(bytes.Length);
        while (!bytes.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf8(
                bytes,
                out Rune rune,
                out int consumed);
            if (status == OperationStatus.Done)
            {
                decoded.Append(rune.ToString());
                bytes = bytes[consumed..];
                continue;
            }

            decoded.Append("\\x");
            decoded.Append(bytes[0].ToString("x2", CultureInfo.InvariantCulture));
            bytes = bytes[1..];
        }

        return decoded.ToString();
    }
}
