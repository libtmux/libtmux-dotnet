using System.Text;

namespace LibTmux.Mcp;

/// <summary>Terminal text cut to fit a budget, with what was cut reported.</summary>
/// <param name="Lines">The text that fits, oldest first.</param>
/// <param name="Truncated">Whether anything was dropped to make it fit.</param>
/// <param name="DroppedLines">How many complete lines were dropped from the start.</param>
/// <param name="DroppedBytes">
/// The exact number of UTF-8 bytes of PANE TEXT dropped from the start. This is
/// not the unit the byte budget is spent in: that bounds the whole serialized
/// result, where the same text is escaped as JSON and carried twice, so for
/// wide characters it can be several times larger than the number here.
/// </param>
/// <remarks>
/// Dropping is always from the oldest end. A terminal's newest line is the one
/// that says what happened, so a budget that discarded it would answer the
/// wrong question. Reporting the loss is what separates a short answer from a
/// wrong one: a reader who cannot see that lines are missing will conclude the
/// pane never printed them.
/// </remarks>
public sealed record BoundedText(
    IReadOnlyList<string> Lines,
    bool Truncated,
    int DroppedLines,
    int DroppedBytes)
{
    /// <summary>An empty result that dropped nothing.</summary>
    public static BoundedText Empty { get; } = new([], false, 0, 0);

    /// <summary>Cuts lines down to a line and byte budget, keeping the newest.</summary>
    /// <param name="lines">The captured text, oldest first.</param>
    /// <param name="maxLines">The most lines to keep, or null for no line limit.</param>
    /// <param name="maxBytes">The most UTF-8 bytes to keep.</param>
    /// <returns>The text that fits, and what it cost to make it fit.</returns>
    public static BoundedText Fit(IReadOnlyList<string> lines, int? maxLines, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        if (lines.Count == 0)
        {
            return Empty;
        }

        int firstEligible = 0;
        if (maxLines is int lineBudget)
        {
            firstEligible = Math.Max(0, lines.Count - Math.Max(lineBudget, 0));
        }

        // A joined capture can hold one logical line far wider than the pane,
        // so apply the byte ceiling to the final newline-joined representation.
        int retainedBytes = 0;
        int start = lines.Count;
        string? clippedFirstLine = null;
        for (int index = lines.Count - 1; index >= firstEligible; index--)
        {
            int separatorBytes = start < lines.Count ? 1 : 0;
            int lineBytes = Encoding.UTF8.GetByteCount(lines[index]);
            if (lineBytes <= maxBytes - retainedBytes - separatorBytes)
            {
                retainedBytes = checked(retainedBytes + separatorBytes + lineBytes);
                start = index;
                continue;
            }

            int remaining = maxBytes - retainedBytes - separatorBytes;
            if (remaining > 0)
            {
                string suffix = Utf8Suffix(lines[index], remaining);
                if (suffix.Length > 0)
                {
                    clippedFirstLine = suffix;
                    retainedBytes = checked(
                        retainedBytes
                        + separatorBytes
                        + Encoding.UTF8.GetByteCount(clippedFirstLine));
                    start = index;
                }
            }

            break;
        }

        int droppedLines = start;
        bool clipped = clippedFirstLine is not null;
        bool truncated = droppedLines > 0 || clipped;
        if (!truncated)
        {
            return new BoundedText(lines, false, 0, 0);
        }

        int totalBytes = JoinedUtf8ByteCount(lines);
        int droppedBytes = checked(totalBytes - retainedBytes);

        string[] kept = new string[lines.Count - start];
        for (int index = 0; index < kept.Length; index++)
        {
            kept[index] = lines[start + index];
        }

        if (clipped)
        {
            kept[0] = clippedFirstLine!;
        }

        return new BoundedText(kept, true, droppedLines, droppedBytes);
    }

    /// <summary>Renders the text as one block, noting any loss at the top.</summary>
    /// <returns>The lines joined by newlines, after any truncation notice.</returns>
    /// <remarks>
    /// The notice leads because a reader who sees it will read what follows as
    /// a tail rather than as the whole pane.
    /// </remarks>
    public string ToDisplayString()
    {
        string body = string.Join('\n', Lines);
        return Truncated
            ? $"[{DroppedLines} complete earlier lines and {DroppedBytes} UTF-8 bytes "
                + $"omitted from the start to fit the budget]\n{body}"
            : body;
    }

    private static int JoinedUtf8ByteCount(IReadOnlyList<string> lines)
    {
        int bytes = 0;
        for (int index = 0; index < lines.Count; index++)
        {
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(lines[index]));
            if (index > 0)
            {
                bytes = checked(bytes + 1);
            }
        }

        return bytes;
    }

    private static string Utf8Suffix(string line, int byteBudget)
    {
        int start = line.Length;
        int bytes = 0;
        while (start > 0)
        {
            int previous = start - 1;
            if (previous > 0
                && char.IsLowSurrogate(line[previous])
                && char.IsHighSurrogate(line[previous - 1]))
            {
                previous--;
            }

            int runeBytes = Encoding.UTF8.GetByteCount(line.AsSpan(previous, start - previous));
            if (runeBytes > byteBudget - bytes)
            {
                break;
            }

            bytes += runeBytes;
            start = previous;
        }

        return line[start..];
    }
}
