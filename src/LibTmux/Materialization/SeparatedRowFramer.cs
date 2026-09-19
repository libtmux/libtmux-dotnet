using System.Text;

namespace LibTmux.Internal;

/// <summary>
/// Decodes tmux rows whose scalars are separated by an unguessable marker.
/// </summary>
/// <remarks>
/// tmux values may contain any byte, including the newline that separates rows
/// and any punctuation a template could reserve, so this framer never searches
/// for a delimiter that a value could hold. It splits on
/// <see cref="FormatProjection.RowSeparator" />, a per-process random marker
/// the template emits around every scalar.
/// </remarks>
internal static class SeparatedRowFramer
{
    /// <summary>Decodes every complete row in one framed payload.</summary>
    /// <param name="payload">Raw bytes tmux wrote to standard output.</param>
    /// <param name="projection">Projection whose fields each row must carry.</param>
    /// <param name="limits">Limits bounding one scalar.</param>
    /// <returns>One dictionary of copied raw values per row.</returns>
    /// <exception cref="LibTmuxException">
    /// The payload is malformed, truncated, oversized, or carries unknown,
    /// duplicated, or missing fields.
    /// </exception>
    internal static IReadOnlyList<IReadOnlyDictionary<string, ReadOnlyMemory<byte>?>> Decode(
        ReadOnlySpan<byte> payload,
        FormatProjection projection,
        TmuxTransportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(limits);

        var rows = new List<IReadOnlyDictionary<string, ReadOnlyMemory<byte>?>>();
        if (payload.IsEmpty)
        {
            return rows;
        }

        ReadOnlySpan<byte> separator = Encoding.ASCII.GetBytes(FormatProjection.RowSeparator);
        int offset = 0;
        while (offset < payload.Length)
        {
            rows.Add(DecodeRow(payload, separator, projection, limits, ref offset));

            // tmux writes a newline after each row; a value may contain one
            // too, but only the byte following the last separator is a
            // terminator.
            ConsumeRowTerminator(payload, ref offset);
        }

        return rows;
    }

    private static Dictionary<string, ReadOnlyMemory<byte>?> DecodeRow(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> separator,
        FormatProjection projection,
        TmuxTransportLimits limits,
        ref int offset)
    {
        var row = new Dictionary<string, ReadOnlyMemory<byte>?>(
            projection.Fields.Count,
            StringComparer.Ordinal);
        foreach (FormatFieldDescriptor field in projection.Fields)
        {
            ReadOnlySpan<byte> value = ReadValue(payload, separator, limits, ref offset);

            // ReadOnlyMemory<T> declares an implicit conversion from T[]?, so a
            // bare null branch here acquires the natural type
            // ReadOnlyMemory<byte> and lands as a present-but-empty value
            // instead of an absent one. The null branch must be typed.
            ReadOnlyMemory<byte>? stored = value.IsEmpty
                ? default(ReadOnlyMemory<byte>?)
                : new ReadOnlyMemory<byte>(value.ToArray());
            if (!row.TryAdd(field.WireName, stored))
            {
                throw new TmuxProtocolException(
                    $"tmux row repeats field '{field.WireName}'.",
                    TmuxDispatchState.Dispatched);
            }
        }

        return row;
    }

    private static ReadOnlySpan<byte> ReadValue(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> separator,
        TmuxTransportLimits limits,
        ref int offset)
    {
        ReadOnlySpan<byte> remainder = payload[offset..];
        int length = remainder.IndexOf(separator);
        if (length < 0)
        {
            throw new TmuxProtocolException(
                "tmux row ended before every field was read.",
                TmuxDispatchState.Dispatched);
        }

        if (length > limits.MaxFramedFieldBytes)
        {
            throw new TmuxProtocolException(
                "tmux value exceeds the framed field limit.",
                TmuxDispatchState.Dispatched);
        }

        offset += length + separator.Length;
        return remainder[..length];
    }

    private static void ConsumeRowTerminator(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (offset >= payload.Length)
        {
            return;
        }

        if (payload[offset] == (byte)'\r'
            && offset + 1 < payload.Length
            && payload[offset + 1] == (byte)'\n')
        {
            offset += 2;
            return;
        }

        if (payload[offset] != (byte)'\n')
        {
            throw new TmuxProtocolException(
                "tmux row is not terminated by a newline.",
                TmuxDispatchState.Dispatched);
        }

        offset++;
    }
}
