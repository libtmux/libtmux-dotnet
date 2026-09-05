using System.Text;
using System.Text.RegularExpressions;

namespace LibTmux.Mcp;

/// <summary>Keeps this server's own bookkeeping out of what a caller reads.</summary>
/// <remarks>
/// <para>
/// Running a command deterministically means appending a rendezvous and a
/// status capture to it, and the shell echoes that like anything else typed.
/// The echo stays on screen for as long as the pane holds it, so it is not
/// enough for a run to hide its own: every later read of that pane would show
/// somebody else's, and a model would reasonably conclude the command printed
/// tmux commands it never ran.
/// </para>
/// <para>
/// The echo is longer than a pane is wide, and tmux stores a wrap as a real
/// line break — so the marker arrives split across rows, and matching row by
/// row finds nothing. Rows are rejoined into the logical line they came from
/// before matching, which is the only form the marker is whole in.
/// </para>
/// </remarks>
internal static partial class PaneText
{
    /// <summary>Removes lines that only exist because this server ran something.</summary>
    /// <param name="lines">The captured rows, oldest first.</param>
    /// <param name="paneWidth">
    /// The pane's width in columns, used to tell a wrapped continuation from a
    /// new line. Pass zero when the rows are already joined (a capture asked for
    /// <c>-J</c>) -- reapplying the width check then reads one long logical line
    /// as continued and swallows the real output beneath it.
    /// </param>
    /// <returns>The rows worth showing.</returns>
    internal static IReadOnlyList<string> Scrub(IReadOnlyList<string> lines, int paneWidth)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return lines;
        }

        Regex marker = MarkerPattern();
        List<string>? kept = null;
        StringBuilder logical = new();

        int start = 0;
        while (start < lines.Count)
        {
            int end = start;
            logical.Clear();
            logical.Append(lines[start]);

            // tmux fills a row to the last column before wrapping, so only a
            // row of exactly that width continues into the next — a longer
            // row means this capture already joined wraps itself.
            while (paneWidth > 0
                && end + 1 < lines.Count
                && lines[end].Length == paneWidth)
            {
                end++;
                logical.Append(lines[end]);
            }

            if (marker.IsMatch(logical.ToString()))
            {
                kept ??= [.. lines.Take(start)];
            }
            else if (kept is not null)
            {
                for (int row = start; row <= end; row++)
                {
                    kept.Add(lines[row]);
                }
            }

            start = end + 1;
        }

        return kept ?? lines;
    }

    /// <summary>Drops everything a run printed before its command's own output.</summary>
    /// <param name="lines">The captured rows, oldest first.</param>
    /// <param name="marker">The marker the run printed before the command.</param>
    /// <param name="paneWidth">The pane's width, or zero when rows are joined.</param>
    /// <returns>The rows after the marker, or all of them when it is not there.</returns>
    /// <remarks>
    /// The marker appears twice: once in the shell's echo of what was pasted,
    /// and once where the shell printed it. The second is the boundary, so the
    /// search takes the last. When neither is present the marker scrolled out
    /// of the pane's history, and dropping everything would hide real output.
    /// </remarks>
    internal static IReadOnlyList<string> AfterBeginMarker(
        IReadOnlyList<string> lines,
        string marker,
        int paneWidth)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);

        int begin = -1;
        StringBuilder logical = new();
        int start = 0;
        while (start < lines.Count)
        {
            int end = start;
            logical.Clear();
            logical.Append(lines[start]);
            while (paneWidth > 0
                && end + 1 < lines.Count
                && lines[end].Length == paneWidth)
            {
                end++;
                logical.Append(lines[end]);
            }

            if (logical.ToString().Contains(marker, StringComparison.Ordinal))
            {
                begin = end;
            }

            start = end + 1;
        }

        return begin < 0 ? lines : [.. lines.Skip(begin + 1)];
    }

    /// <summary>Matches the channel and option names a run leaves behind.</summary>
    /// <remarks>
    /// Anchored to the exact shape minted by <see cref="WriteTools.RunToken" />
    /// so that ordinary text mentioning the prefix survives. The begin marker
    /// is spelled in halves in the payload, so the echo carries a five-digit
    /// form as well as the ten-digit one it prints.
    /// </remarks>
    [GeneratedRegex(@"@?lt_[rsb]_[0-9a-f]{5,10}", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();
}
