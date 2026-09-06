using System.Collections.Concurrent;
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
    private const int RememberedTokens = 64;
    private static readonly ConcurrentQueue<string> Minted = new();

    /// <summary>Records a token this process minted, so its echo is known.</summary>
    /// <param name="id">The run token's identifier.</param>
    /// <remarks>
    /// Shape alone cannot separate this server's bookkeeping from a caller's
    /// text: widened, it deleted a build log line; narrowed, it left the
    /// payload's own rows on screen. A token that was actually minted is not a
    /// guess, so it catches a row that wrapping split away from every shape
    /// while never matching text a caller wrote. The shapes stay for
    /// bookkeeping a previous server process left behind.
    /// </remarks>
    internal static void Remember(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Minted.Enqueue(id);
        while (Minted.Count > RememberedTokens && Minted.TryDequeue(out _))
        {
        }
    }

    private static bool CarriesMintedToken(string logical)
    {
        foreach (string id in Minted)
        {
            if (logical.Contains(id, StringComparison.Ordinal)
                || logical.Contains($"lt_b_{id[..5]}", StringComparison.Ordinal)
                || logical.Contains($"lt_e_{id[..5]}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Marks the rows a run's echoed payload occupies.</summary>
    /// <remarks>
    /// Per-row matching cannot cover the echo: it is one shell command line
    /// wrapped across rows, and a middle row carries no token at all — which
    /// is how <c>set-option</c> and <c>wait-for</c> stayed readable through
    /// search and could wake a wait. The echo runs from the row naming the
    /// begin marker to the row naming the rendezvous channel, and both ends
    /// are minted ids, so the span is exact and matches nothing a caller
    /// wrote.
    /// </remarks>
    private static bool[] PayloadRows(IReadOnlyList<string> lines)
    {
        bool[] payload = new bool[lines.Count];
        foreach (string id in Minted)
        {
            string head = $"lt_b_{id[..5]}";
            string channel = $"lt_r_{id}";
            int first = -1;
            for (int row = 0; row < lines.Count; row++)
            {
                if (first < 0 && lines[row].Contains(head, StringComparison.Ordinal))
                {
                    first = row;
                }

                if (first >= 0 && lines[row].Contains(channel, StringComparison.Ordinal))
                {
                    for (int span = first; span <= row; span++)
                    {
                        payload[span] = true;
                    }

                    first = -1;
                }
            }
        }

        return payload;
    }

    /// <summary>Removes lines that only exist because this server ran something.</summary>    /// <summary>Removes lines that only exist because this server ran something.</summary>
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
        bool[] payload = PayloadRows(lines);
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

            string joined = logical.ToString();
            bool inPayload = false;
            for (int row = start; row <= end && !inPayload; row++)
            {
                inPayload = payload[row];
            }

            if (inPayload || marker.IsMatch(joined) || CarriesMintedToken(joined))
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

    /// <summary>Drops the marker a run printed after its command, and all that follows.</summary>
    /// <param name="lines">The rows left after the begin marker, oldest first.</param>
    /// <param name="marker">The marker the run printed once the command returned.</param>
    /// <param name="paneWidth">The pane's width, or zero when rows are joined.</param>
    /// <returns>The rows the command itself printed.</returns>
    /// <remarks>
    /// The window needs both bounds. Dropping everything before the begin
    /// marker still left whatever the shell drew afterwards — its next prompt —
    /// reported as command output, which a short prompt hid because the
    /// since-baseline diff happened to absorb it and a wrapped one did not.
    /// <para>
    /// The marker does not always start a row: a command whose last write had
    /// no trailing newline leaves the cursor mid-row, and the marker is printed
    /// from there. So the row carrying it is truncated rather than dropped, and
    /// what the command printed before it is kept.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> BeforeEndMarker(
        IReadOnlyList<string> lines,
        string marker,
        int paneWidth)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);

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

            int at = logical.ToString().IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                start = end + 1;
                continue;
            }

            List<string> kept = [.. lines.Take(start)];
            int consumed = 0;
            for (int row = start; row <= end; row++)
            {
                if (consumed + lines[row].Length > at)
                {
                    string head = lines[row][..(at - consumed)];
                    if (head.Length > 0)
                    {
                        kept.Add(head);
                    }

                    break;
                }

                kept.Add(lines[row]);
                consumed += lines[row].Length;
            }

            return kept;
        }

        return lines;
    }

    /// <summary>Matches the channel and option names a run leaves behind.</summary>
    /// <remarks>
    /// Anchored to the exact shape minted by <see cref="WriteTools.RunToken" />
    /// so that ordinary text mentioning the prefix survives. The begin marker
    /// is spelled in halves in the payload, so the echo carries no ten-digit
    /// form — but it always carries the two quoted halves adjacent, which is
    /// a shape a caller's own output does not have. Matching that rather than
    /// widening the digit count keeps a line like <c>lt_b_abcde</c> in a
    /// user's build log, which a five-digit minimum would have deleted.
    /// <para>
    /// The status assignment is matched too, because rejoining wrapped rows
    /// cannot be relied on: tmux trims a row's trailing spaces, so a wrapped
    /// row is not always exactly the pane's width and the join stops early.
    /// That left the payload's tail row — the one naming set-option and
    /// wait-for — orphaned from the marker below it, on every read path.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"@?lt_[rsbe]_[0-9a-f]{10}|'lt_[be]_[0-9a-f]{5}' '[0-9a-f]{5}'|__lt=\$\?",
        RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();
}
