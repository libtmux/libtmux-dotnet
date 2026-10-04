using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace LibTmux.Internal;

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
    private static bool[] PayloadRows(IReadOnlyList<string> lines, IEnumerable<string> tokens)
    {
        bool[] payload = new bool[lines.Count];
        foreach (string id in tokens)
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
        HashSet<string> previousTokens = PreviousRunTokens(lines, paneWidth);
        bool[] payload = PayloadRows(lines, Minted.Concat(previousTokens));
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

            if (inPayload || marker.IsMatch(joined) || CarriesMintedToken(joined)
                || CarriesPreviousToken(joined, previousTokens))
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

    // Ten-hex run tokens were minted by the previous published runner. Their
    // shape alone is not proof: accept one only when the split begin, status
    // option and rendezvous channel agree in this capture.
    private static HashSet<string> PreviousRunTokens(IReadOnlyList<string> lines, int paneWidth)
    {
        HashSet<string> beginnings = [];
        HashSet<string> statuses = [];
        HashSet<string> channels = [];
        StringBuilder logical = new();
        int start = 0;
        while (start < lines.Count)
        {
            int end = start;
            logical.Clear();
            logical.Append(lines[start]);
            while (paneWidth > 0 && end + 1 < lines.Count && lines[end].Length == paneWidth)
            {
                end++;
                logical.Append(lines[end]);
            }

            foreach (Match match in PreviousMarkerPattern().Matches(logical.ToString()))
            {
                string id = match.Groups["id"].Success
                    ? match.Groups["id"].Value
                    : match.Groups["head"].Value + match.Groups["tail"].Value;
                if (match.Value.StartsWith("'lt_b_", StringComparison.Ordinal))
                {
                    beginnings.Add(id);
                }
                else if (match.Value.StartsWith("@lt_s_", StringComparison.Ordinal))
                {
                    statuses.Add(id);
                }
                else if (match.Value.StartsWith("lt_r_", StringComparison.Ordinal))
                {
                    channels.Add(id);
                }
            }

            start = end + 1;
        }

        beginnings.IntersectWith(statuses);
        beginnings.IntersectWith(channels);
        return beginnings;
    }

    private static bool CarriesPreviousToken(string logical, HashSet<string> tokens)
    {
        foreach (Match match in PreviousMarkerPattern().Matches(logical))
        {
            string id = match.Groups["id"].Success
                ? match.Groups["id"].Value
                : match.Groups["head"].Value + match.Groups["tail"].Value;
            if (tokens.Contains(id))
            {
                return true;
            }
        }

        return false;
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

    private static bool IsWordChar(char character) => char.IsLetterOrDigit(character) || character == '_';

    /// <summary>Removes every standalone occurrence of <paramref name="echo" /> from <paramref name="text" />.</summary>
    /// <remarks>
    /// An occurrence counts only where it is not part of a longer run of word
    /// characters on either side - what tells a short typed answer apart from
    /// a longer word that merely contains it: <c>y</c> comes off <c>$ y</c>
    /// and stays inside <c>ready</c>. This is what lets a whole recorded line
    /// (<c>echo MARKER</c>) be removed as the exact thing that was typed,
    /// without also erasing an unrelated later line whose real output happens
    /// to repeat one of its words.
    /// </remarks>
    internal static string WithoutEcho(string text, string echo)
    {
        if (echo.Length == 0 || text.Length == 0)
        {
            return text;
        }

        StringBuilder result = new(text.Length);
        int cursor = 0;
        int from = 0;
        while (true)
        {
            int at = text.IndexOf(echo, from, StringComparison.Ordinal);
            if (at < 0)
            {
                break;
            }

            int end = at + echo.Length;
            bool opens = at == 0 || !IsWordChar(text[at - 1]) || !IsWordChar(echo[0]);
            bool closes = end == text.Length || !IsWordChar(text[end]) || !IsWordChar(echo[^1]);
            if (opens && closes)
            {
                result.Append(text, cursor, at - cursor);
                cursor = end;
                from = end;
            }
            else
            {
                from = at + 1;
            }
        }

        result.Append(text, cursor, text.Length - cursor);
        return result.ToString();
    }

    /// <summary><see cref="WithoutEcho" />, applied for every text in <paramref name="echoes" />.</summary>
    internal static string WithoutEchoes(string text, IEnumerable<string> echoes)
    {
        string result = text;
        foreach (string echo in echoes.Where(echo => echo.Length > 0))
        {
            result = WithoutEcho(result, echo);
        }

        return result;
    }

    /// <summary>Builds what removes typed text from the rows a pane shows afterwards.</summary>
    /// <param name="typed">The text typed; each of its lines is removed on its own.</param>
    /// <returns>A function from screen rows to the same rows without the typed lines.</returns>
    /// <remarks>
    /// <para>
    /// A typed line longer than the pane continues on the next row, and tmux
    /// trims the spaces a row ends with, so a wrap can fall between any two
    /// characters and swallow a space. The rows are searched as one text in
    /// which a row break may stand inside an occurrence, and removing one keeps
    /// its row breaks, so the rows stay in place.
    /// </para>
    /// <para>
    /// A shell still echoing a line shows only its start, at the end of the
    /// screen; that start is removed too. As in <see cref="WithoutEcho" />, an
    /// occurrence counts only where it is not part of a longer word, so output
    /// identical to a typed line is removed with it.
    /// </para>
    /// </remarks>
    internal static Func<IReadOnlyList<string>, IReadOnlyList<string>> TypedEchoRemover(string typed) =>
        new TypedEchoProjection(typed, null, CancellationToken.None).Project;

    internal sealed class TypedEchoProjection
    {
        internal const int MaximumTypedBytes = 64 * 1024;
        private const int MaximumProjectionWork = 4 * PaneWaitRequest.MaximumMatchWorkBytes;
        private static readonly TimeSpan MaximumRegexTime = TimeSpan.FromSeconds(1);
        private readonly string[] lines;
        private readonly CancellationToken cancellationToken;
        private readonly Func<TimeSpan>? remainingBudget;
        private int work;

        internal TypedEchoProjection(
            string typed,
            Func<TimeSpan>? remainingBudget,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(typed);
            this.cancellationToken = cancellationToken;
            this.remainingBudget = remainingBudget;
            Check();
            if (typed.Length > MaximumTypedBytes
                || Encoding.UTF8.GetByteCount(typed) > MaximumTypedBytes)
            {
                throw new ArgumentException(
                    $"Typed echo text exceeds {MaximumTypedBytes} UTF-8 bytes.", nameof(typed));
            }

            lines = typed.Split(['\r', '\n'])
                .Select(line => line.TrimEnd(' '))
                .Where(line => line.Length > 0)
                .ToArray();
            Check();
        }

        internal IReadOnlyList<string>? LastInput { get; private set; }

        internal IReadOnlyList<string> LastCompleted { get; private set; } = [];

        internal IReadOnlyList<string> Project(IReadOnlyList<string> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            Check();
            if (rows.Count == 0)
            {
                LastInput = rows;
                LastCompleted = rows;
                return rows;
            }

            int bytes = 0;
            for (int index = 0; index < rows.Count; index++)
            {
                Check();
                int separator = index == 0 ? 0 : 1;
                if (separator > PaneWaitRequest.MaximumMatchWorkBytes - bytes)
                {
                    throw new PaneTextWaiter.MatchWorkExceededException();
                }

                bytes += separator;
                int lineBytes = Encoding.UTF8.GetByteCount(rows[index]);
                if (lineBytes > PaneWaitRequest.MaximumMatchWorkBytes - bytes)
                {
                    throw new PaneTextWaiter.MatchWorkExceededException();
                }

                bytes += lineBytes;
            }

            string text = string.Join('\n', rows);
            Check();
            foreach (string line in lines)
            {
                Check();
                if (line.Length - line.AsSpan().Count(' ') <= text.Length)
                {
                    text = WithoutWrapped(text, line, this, ref work);
                }
            }

            foreach (string line in lines)
            {
                text = WithoutEchoInProgress(text, line, this, ref work);
            }

            Check();
            string[] projected = text.Split('\n');
            LastInput = rows;
            LastCompleted = projected;
            return projected;
        }

        internal void Charge(ref int work, int amount = 1)
        {
            if (amount > MaximumProjectionWork - work)
            {
                throw new ProjectionWorkExceededException();
            }

            work += amount;
            if ((work & 1023) < amount)
            {
                Check();
            }
        }

        internal Regex Occurrence(string pattern, Regex? previous)
        {
            Check();
            TimeSpan available = remainingBudget?.Invoke() ?? MaximumRegexTime;
            if (available <= TimeSpan.Zero)
            {
                throw new ProjectionDeadlineException();
            }

            if (previous is not null && previous.MatchTimeout <= available)
            {
                return previous;
            }

            long timeoutTicks = Math.Min(MaximumRegexTime.Ticks, available.Ticks);
            // Keep a little of the deadline for cancellation and result capture.
            timeoutTicks = Math.Max(1, timeoutTicks - Math.Max(1, timeoutTicks / 10));
            Regex occurrence = new(
                pattern, RegexOptions.CultureInvariant, TimeSpan.FromTicks(timeoutTicks));
            Check();
            return occurrence;
        }

        internal void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remainingBudget is not null && remainingBudget() <= TimeSpan.Zero)
            {
                throw new ProjectionDeadlineException();
            }
        }

        internal sealed class ProjectionDeadlineException()
            : TimeoutException("Typed echo projection exceeded the pane wait deadline.");

        internal sealed class ProjectionWorkExceededException()
            : IOException("Typed echo projection exceeded its bounded work budget.");
    }

    private static string WithoutWrapped(
        string text,
        string line,
        TypedEchoProjection projection,
        ref int work)
    {
        StringBuilder? result = null;
        int cursor = 0;
        string pattern = WrappedOccurrence(line, projection, ref work);
        Regex? occurrence = null;
        Match match = NextWrappedMatch(text, pattern, 0, projection, ref occurrence, ref work);
        while (match.Success)
        {
            int end = match.Index + match.Length;
            bool opens = match.Index == 0 || !IsWordChar(text[match.Index - 1]) || !IsWordChar(line[0]);
            bool closes = end == text.Length || !IsWordChar(text[end]) || !IsWordChar(line[^1]);
            if (opens && closes)
            {
                result ??= new StringBuilder(text.Length);
                result.Append(text, cursor, match.Index - cursor).Append('\n', match.ValueSpan.Count('\n'));
                cursor = end;
                match = NextWrappedMatch(text, pattern, end, projection, ref occurrence, ref work);
            }
            else
            {
                match = NextWrappedMatch(text, pattern, match.Index + 1, projection,
                    ref occurrence, ref work);
            }
        }

        return result is null ? text : result.Append(text, cursor, text.Length - cursor).ToString();
    }

    private static Match NextWrappedMatch(
        string text,
        string pattern,
        int start,
        TypedEchoProjection projection,
        ref Regex? occurrence,
        ref int work)
    {
        projection.Charge(ref work, Math.Min(1024, text.Length - start + 1));
        occurrence = projection.Occurrence(pattern, occurrence);
        Match found = occurrence.Match(text, start);
        projection.Check();
        return found;
    }

    // Each rendered row consumes a literal suffix of a typed prefix. Compare
    // all candidate prefixes to that row in one Z pass, then apply the exact
    // space skip tmux makes at a row break.
    private static string WithoutEchoInProgress(
        string text,
        string line,
        TypedEchoProjection projection,
        ref int work)
    {
        int end = text.Length;
        while (end > 0 && text[end - 1] is ' ' or '\n')
        {
            projection.Charge(ref work);
            end--;
        }

        if (end == 0 || line.Length < 2)
        {
            return text;
        }

        int visibleCharacters = 0;
        for (int index = 0; index < end; index++)
        {
            projection.Charge(ref work);
            if (text[index] != '\n')
            {
                visibleCharacters++;
            }
        }

        int[] previousNonSpace = new int[line.Length];
        int preceding = -1;
        for (int index = 0; index < line.Length; index++)
        {
            projection.Charge(ref work);
            if (line[index] != ' ')
            {
                preceding = index;
            }

            previousNonSpace[index] = preceding;
        }

        int[] current = new int[line.Length];
        int[] next = new int[line.Length];
        List<int> active = [];
        List<int> nextActive = [];
        int bestLength = 0;
        int bestStart = -1;

        void Record(int length, int start)
        {
            if (length > bestLength
                && (start == 0 || !IsWordChar(text[start - 1]) || !IsWordChar(line[0])))
            {
                bestLength = length;
                bestStart = start;
            }
        }

        void AddNext(int position, int length)
        {
            if (length <= bestLength)
            {
                return;
            }

            if (next[position] == 0)
            {
                nextActive.Add(position);
            }

            next[position] = Math.Max(next[position], length);
        }

        void Advance()
        {
            foreach (int position in active)
            {
                current[position] = 0;
            }

            (current, next) = (next, current);
            (active, nextActive) = (nextActive, active);
            nextActive.Clear();
        }

        int requiredCharacters = 0;
        for (int length = 1; length < line.Length; length++)
        {
            projection.Charge(ref work);
            if (line[length - 1] != ' ')
            {
                requiredCharacters++;
            }

            if (requiredCharacters > visibleCharacters)
            {
                break;
            }

            int position = previousNonSpace[length - 1];
            if (position < 0)
            {
                Record(length, end);
            }
            else
            {
                if (current[position] == 0)
                {
                    active.Add(position);
                }

                current[position] = length;
            }
        }

        int segmentEnd = end - 1;
        while (active.Count > 0 && segmentEnd >= 0 && bestLength < line.Length - 1)
        {
            projection.Check();
            int separator = text.LastIndexOf('\n', segmentEnd);
            int segmentStart = separator + 1;
            int segmentLength = segmentEnd - segmentStart + 1;
            if (segmentLength > 0)
            {
                int maximumPosition = active.Max();
                int comparedLength = Math.Min(segmentLength, maximumPosition + 1);
                int[] lcp = ReversedSegmentLcp(
                    text, segmentEnd, comparedLength, line, maximumPosition,
                    projection, ref work);
                foreach (int position in active)
                {
                    projection.Charge(ref work);
                    int length = current[position];
                    if (length <= bestLength)
                    {
                        continue;
                    }

                    int matched = lcp[comparedLength + 1 + maximumPosition - position];
                    if (matched < Math.Min(position + 1, segmentLength))
                    {
                        continue;
                    }

                    if (position + 1 <= segmentLength)
                    {
                        Record(length, segmentEnd - position);
                    }
                    else
                    {
                        AddNext(position - segmentLength, length);
                    }
                }

                Advance();
            }

            if (separator < 0 || active.Count == 0)
            {
                break;
            }

            foreach (int position in active)
            {
                projection.Charge(ref work);
                int length = current[position];
                if (length <= bestLength)
                {
                    continue;
                }

                int beforeSpaces = previousNonSpace[position];
                if (beforeSpaces < 0)
                {
                    Record(length, separator);
                }
                else
                {
                    AddNext(beforeSpaces, length);
                }
            }

            Advance();
            segmentEnd = separator - 1;
            while (segmentEnd >= 0 && text[segmentEnd] == '\n')
            {
                projection.Charge(ref work);
                segmentEnd--;
            }
        }

        if (bestStart < 0)
        {
            return text;
        }

        int breaks = text.AsSpan(bestStart, end - bestStart).Count('\n');
        return string.Concat(text.AsSpan(0, bestStart), new string('\n', breaks), text.AsSpan(end));
    }

    private static int[] ReversedSegmentLcp(
        string text,
        int segmentEnd,
        int segmentLength,
        string line,
        int maximumPosition,
        TypedEchoProjection projection,
        ref int work)
    {
        int total = segmentLength + maximumPosition + 2;
        int[] z = new int[total];
        int ValueAt(int index) => index < segmentLength
            ? text[segmentEnd - index]
            : index == segmentLength
                ? -1
                : line[maximumPosition - (index - segmentLength - 1)];

        int left = 0;
        int right = 0;
        for (int index = 1; index < total; index++)
        {
            projection.Charge(ref work);
            if (index <= right)
            {
                z[index] = Math.Min(right - index + 1, z[index - left]);
            }

            while (index + z[index] < total
                && ValueAt(z[index]) == ValueAt(index + z[index]))
            {
                projection.Charge(ref work);
                z[index]++;
            }

            if (index + z[index] - 1 > right)
            {
                left = index;
                right = index + z[index] - 1;
            }
        }

        return z;
    }

    // Each character may be followed by a wrap; a run of spaces may be cut
    // short by one, since tmux drops the spaces a wrapped row ends with.
    private static string WrappedOccurrence(
        string line,
        TypedEchoProjection projection,
        ref int work)
    {
        StringBuilder pattern = new(line.Length * 4);
        for (int index = 0; index < line.Length; index++)
        {
            projection.Charge(ref work);
            if (line[index] == ' ')
            {
                while (index + 1 < line.Length && line[index + 1] == ' ')
                {
                    projection.Charge(ref work);
                    index++;
                }

                pattern.Append("(?: +| *\n *)");
            }
            else
            {
                pattern.Append(Regex.Escape(line[index].ToString())).Append(index + 1 < line.Length ? "\n?" : string.Empty);
            }
        }

        projection.Check();
        return pattern.ToString();
    }

    /// <summary>Matches the channel and option names a run leaves behind.</summary>
    /// <remarks>
    /// Matches full random tokens and their two quoted halves in a sourced
    /// payload. A bare prefix in a caller's output remains visible. The exact
    /// private script path also removes the echoed source command.
    /// <para>
    /// The status assignment is matched too, because rejoining wrapped rows
    /// cannot be relied on: tmux trims a row's trailing spaces, so a wrapped
    /// row is not always exactly the pane's width and the join stops early.
    /// That left the payload's tail row — the one naming set-option and
    /// wait-for — orphaned from the marker below it, on every read path.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"@?lt_[rsbe]_[0-9a-f]{32}|'lt_[be]_[0-9a-f]{5}' '[0-9a-f]{27}'|\. '[^\r\n]*/libtmux-run-[0-9a-f]{32}-[A-Za-z0-9]{6}/run'|__lt=\$\?",
        RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(
        @"@?lt_[rsbe]_(?<id>[0-9a-f]{10})(?![0-9a-f])|'lt_[be]_(?<head>[0-9a-f]{5})' '(?<tail>[0-9a-f]{5})'",
        RegexOptions.CultureInvariant)]
    private static partial Regex PreviousMarkerPattern();
}
