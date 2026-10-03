using System.Runtime.Versioning;

namespace LibTmux.Internal;

/// <summary>What a pane has printed, and what is new since last time.</summary>
/// <param name="State">The grid state the read saw.</param>
/// <param name="Lines">The rows the read is reporting.</param>
/// <param name="CursorRows">The rows from the cursor row down, for the next cursor.</param>
/// <param name="LinesMissed">Whether scrollback dropped rows this read never saw.</param>
/// <param name="AnchorLost">Whether the previous position could not be found again.</param>
[UnsupportedOSPlatform("windows")]
internal sealed record PaneRead(
    PaneGridState State,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> CursorRows,
    bool LinesMissed,
    bool AnchorLost);

/// <summary>Names why a pane could not be read consistently.</summary>
internal enum PaneReadFailure
{
    /// <summary>The pane's program has exited.</summary>
    Dead,

    /// <summary>The pane changed during every read attempt.</summary>
    Unstable,

    /// <summary>tmux reported no state for the pane.</summary>
    Unreported,

    /// <summary>The pane runs a different process than the cursor recorded.</summary>
    Replaced,
}

/// <summary>Reads a pane so that two reads do not overlap or skip.</summary>
/// <remarks>
/// <para>
/// A pane is a grid, not a log. Rows move up as it scrolls, get rewritten in
/// place by a prompt redraw, and are freed once <c>history-limit</c> is
/// reached. Reading "what is new" therefore has to survive all three, and the
/// only way to know a read was consistent is to check that the grid did not
/// move underneath it.
/// </para>
/// <para>
/// The algorithm mirrors the one proven in the Python server: sample the state
/// before and after each capture and retry when they differ; fall back to a
/// hash search when eviction may have rebased the rows; and say so plainly
/// when the anchor is gone rather than silently reporting the whole screen as
/// new.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class PaneReader
{
    private const int StableReadAttempts = 3;

    /// <summary>Describes a read failure without naming any caller's tools.</summary>
    internal static Exception Failure(PaneReadFailure failure, Pane pane) => new TmuxPaneException(
        failure switch
        {
            PaneReadFailure.Dead =>
                $"Pane {pane.Id} is dead: the program in it has exited. Respawn the pane to start it again.",
            PaneReadFailure.Unstable =>
                $"Pane {pane.Id} changed during every snapshot attempt. Try again when its output is less busy.",
            PaneReadFailure.Replaced =>
                $"Pane {pane.Id} is running a different process than when the read began.",
            _ => $"tmux did not report the state of pane {pane.Id}. It may have just closed.",
        },
        pane.Id);

    /// <summary>Reads what is on screen now, with no previous position.</summary>
    /// <param name="pane">The pane to read.</param>
    /// <param name="baselinePid">The pid the caller last saw, or null on a first read.</param>
    /// <param name="fail">Builds the exception for a read that cannot be completed.</param>
    /// <param name="cancellationToken">Cancels the tmux queries.</param>
    /// <returns>The read.</returns>
    internal static async Task<PaneRead> ReadVisibleAsync(
        Pane pane,
        string? baselinePid,
        Func<PaneReadFailure, Pane, Exception> fail,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < StableReadAttempts; attempt++)
        {
            PaneGridState before = await RequireStateAsync(pane, fail, cancellationToken)
                .ConfigureAwait(false);
            if (baselinePid is null && before.Dead)
            {
                throw fail(PaneReadFailure.Dead, pane);
            }

            IReadOnlyList<string> lines = await CaptureAsync(pane, null, cancellationToken)
                .ConfigureAwait(false);
            PaneGridState after = await RequireStateAsync(pane, fail, cancellationToken)
                .ConfigureAwait(false);

            if (before == after)
            {
                IReadOnlyList<string> cursorRows = CursorRowsFromCapture(lines, 0, after);
                return new PaneRead(after, lines, cursorRows, false, baselinePid is not null);
            }
        }

        throw fail(PaneReadFailure.Unstable, pane);
    }

    /// <summary>Reads what a pane has printed since a cursor was issued.</summary>
    /// <param name="pane">The pane to read.</param>
    /// <param name="cursor">Where the last read finished.</param>
    /// <param name="fail">Builds the exception for a read that cannot be completed.</param>
    /// <param name="cancellationToken">Cancels the tmux queries.</param>
    /// <returns>The read.</returns>
    internal static async Task<PaneRead> ReadSinceAsync(
        Pane pane,
        PaneCursor cursor,
        Func<PaneReadFailure, Pane, Exception> fail,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < StableReadAttempts; attempt++)
        {
            PaneGridState before = await RequireStateAsync(pane, fail, cancellationToken)
                .ConfigureAwait(false);
            RaiseIfPaneReplaced(pane, before, cursor, fail);

            if (AnchorLost(cursor, before))
            {
                PaneRead missed = await ReadVisibleAsync(pane, cursor.PanePid, fail, cancellationToken)
                    .ConfigureAwait(false);
                return missed with { LinesMissed = true, AnchorLost = true };
            }

            bool trimRisk = TrimRisk(cursor, before);
            int previousStart = cursor.AnchorAbsolute - before.HistorySize;
            int captureStart = trimRisk
                ? -before.HistorySize
                : Math.Min(previousStart, before.CursorY);
            IReadOnlyList<string> capturedRows = trimRisk
                ? await CaptureAsync(pane, int.MinValue, cancellationToken).ConfigureAwait(false)
                : captureStart >= before.PaneHeight
                    ? []
                    : await CaptureAsync(pane, captureStart, cancellationToken).ConfigureAwait(false);

            PaneGridState after = await RequireStateAsync(pane, fail, cancellationToken)
                .ConfigureAwait(false);
            RaiseIfPaneReplaced(pane, after, cursor, fail);

            if (before != after)
            {
                continue;
            }

            int previousOffset;
            if (trimRisk)
            {
                int? match = FindUniqueAnchor(capturedRows, cursor, cancellationToken);
                if (match is null)
                {
                    PaneRead missed = await ReadVisibleAsync(pane, cursor.PanePid, fail, cancellationToken)
                        .ConfigureAwait(false);
                    return missed with { LinesMissed = true, AnchorLost = true };
                }

                previousOffset = match.Value;
            }
            else
            {
                previousOffset = checked(previousStart - captureStart);
            }

            int cursorOffset = checked(after.CursorY - captureStart);
            List<string> reported = ReportRows(
                capturedRows,
                previousOffset,
                cursorOffset,
                cursor);
            IReadOnlyList<string> cursorRows = RowsFromOffset(capturedRows, cursorOffset);

            return new PaneRead(after, reported, cursorRows, false, false);
        }

        PaneRead busy = await ReadVisibleAsync(pane, cursor.PanePid, fail, cancellationToken)
            .ConfigureAwait(false);
        return busy with { LinesMissed = true, AnchorLost = true };
    }

    /// <summary>Captures rows from a pane.</summary>
    /// <param name="pane">The pane to capture.</param>
    /// <param name="start">
    /// The first row: null for the top of the visible screen, a negative number
    /// for that many rows back into scrollback, or <see cref="int.MinValue" />
    /// for everything tmux still holds.
    /// </param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The rows.</returns>
    internal static async Task<IReadOnlyList<string>> CaptureAsync(
        Pane pane,
        int? start,
        CancellationToken cancellationToken)
    {
        CapturePaneRequest? request = start switch
        {
            null => null,
            int.MinValue => new CapturePaneRequest { StartLine = CapturePanePosition.BeginningOfHistory },
            int value => new CapturePaneRequest { StartLine = new CapturePanePosition(value) },
        };
        return await pane.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> CursorRowsFromCapture(
        IReadOnlyList<string> rows,
        int captureStart,
        PaneGridState state)
    {
        if (state.CursorY >= state.PaneHeight)
        {
            return [];
        }

        long offset = (long)state.CursorY - captureStart;
        return offset is >= 0 and <= int.MaxValue
            ? RowsFromOffset(rows, (int)offset)
            : [];
    }

    private static IReadOnlyList<string> RowsFromOffset(
        IReadOnlyList<string> rows,
        int offset) =>
        offset >= 0 && offset < rows.Count ? [.. rows.Skip(offset)] : [];

    private static List<string> ReportRows(
        IReadOnlyList<string> capturedRows,
        int previousOffset,
        int cursorOffset,
        PaneCursor cursor)
    {
        IReadOnlyList<string> previousRows = RowsFromOffset(capturedRows, previousOffset);
        List<string> reported = DropAlreadySeen(previousRows, cursor);
        // Rows above the previous anchor carry no recorded digest, so a cursor
        // that moved up reports them rather than risk dropping a rewrite.
        if (cursorOffset < previousOffset)
        {
            reported.InsertRange(
                0,
                capturedRows.Skip(cursorOffset).Take(previousOffset - cursorOffset));
        }

        return reported;
    }

    private static async Task<PaneGridState> RequireStateAsync(
        Pane pane,
        Func<PaneReadFailure, Pane, Exception> fail,
        CancellationToken cancellationToken)
    {
        PaneGridState? state = await PaneGridState.ReadAsync(pane, cancellationToken)
            .ConfigureAwait(false);
        return state ?? throw fail(PaneReadFailure.Unreported, pane);
    }

    private static void RaiseIfPaneReplaced(
        Pane pane,
        PaneGridState state,
        PaneCursor cursor,
        Func<PaneReadFailure, Pane, Exception> fail)
    {
        if (!string.Equals(state.PanePid, cursor.PanePid, StringComparison.Ordinal))
        {
            throw fail(PaneReadFailure.Replaced, pane);
        }
    }

    private static bool AnchorLost(PaneCursor cursor, PaneGridState state)
    {
        if (cursor.AnchorAbsolute > state.HistorySize + state.PaneHeight - 1)
        {
            return true;
        }

        // clear-history resets the grid to nothing, which destroys the anchor
        // whatever the pane's height is.
        if (state.HistorySize == 0 && cursor.HistorySize > 0)
        {
            return true;
        }

        // Shrinking history means rows were freed; a taller pane means rows were
        // pulled back out of history into view, which frees nothing.
        return state.HistorySize < cursor.HistorySize && state.PaneHeight <= cursor.PaneHeight;
    }

    private static bool TrimRisk(PaneCursor cursor, PaneGridState state)
    {
        if (state.HistoryLimit <= 0)
        {
            return true;
        }

        // tmux frees the oldest history in batches rather than a line at a time,
        // so the risk starts before the limit is reached exactly.
        int batch = Math.Max(state.HistoryLimit / 10, 1);
        int floor = state.HistoryLimit - batch;
        return cursor.HistorySize >= floor || state.HistorySize >= floor;
    }

    internal static int? FindUniqueAnchor(
        IReadOnlyList<string> rows,
        PaneCursor cursor,
        CancellationToken cancellationToken)
    {
        if (cursor.AnchorHash is null)
        {
            return null;
        }

        int fingerprintLength = checked(cursor.BelowCount + 1);
        if (rows.Count < fingerprintLength)
        {
            return null;
        }

        int? match = null;
        for (int index = 0; index + fingerprintLength <= rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    PaneCursor.HashLine(rows[index]),
                    cursor.AnchorHash,
                    StringComparison.Ordinal)
                || (cursor.BelowCount > 0
                    && !string.Equals(
                        PaneCursor.HashRows(rows, index + 1, cursor.BelowCount),
                        cursor.BelowHash,
                        StringComparison.Ordinal)))
            {
                continue;
            }

            // Two matches mean the fingerprint does not identify a place. A
            // guess would silently report the wrong rows as new.
            if (match is not null)
            {
                return null;
            }

            match = index;
        }

        return match;
    }

    internal static List<string> DropAlreadySeen(
        IReadOnlyList<string> rows,
        PaneCursor cursor)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        List<string> kept = [];
        if (cursor.AnchorHash is null
            || !string.Equals(PaneCursor.HashLine(rows[0]), cursor.AnchorHash, StringComparison.Ordinal))
        {
            // The anchor row was rewritten since it was seen, so it is new text.
            kept.Add(rows[0]);
        }

        int index = 1;
        if (cursor.SuffixCount > 0
            && rows.Count - index >= cursor.SuffixCount
            && string.Equals(
                PaneCursor.HashRows(rows, index, cursor.SuffixCount),
                cursor.SuffixHash,
                StringComparison.Ordinal))
        {
            return Report(kept, rows, index + cursor.SuffixCount);
        }

        // A pane below the cursor is redrawn a row at a time, so comparing the
        // block as a whole would replay every row beside the one that changed.
        byte[]? digests = cursor.TrackedRowDigests();
        int tracked = digests is null
            ? 0
            : Math.Min(cursor.BelowCount, Math.Max(rows.Count - index, 0));
        for (int row = 0; row < tracked; row++)
        {
            if (!PaneCursor.TrackedRowUnchanged(digests!, row, rows[index + row]))
            {
                kept.Add(rows[index + row]);
            }
        }

        return Report(kept, rows, index + tracked);
    }

    private static List<string> Report(List<string> kept, IReadOnlyList<string> rows, int from)
    {
        for (int row = from; row < rows.Count; row++)
        {
            kept.Add(rows[row]);
        }

        return kept;
    }
}
