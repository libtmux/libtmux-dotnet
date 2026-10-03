using System.Runtime.Versioning;

namespace LibTmux.Internal;

/// <summary>What a pane has printed, and what is new since last time.</summary>
/// <param name="State">The grid state the read saw.</param>
/// <param name="Lines">The rows the read is reporting.</param>
/// <param name="CursorRows">The rows from the cursor row down, for the next cursor.</param>
/// <param name="LinesMissed">Whether scrollback dropped rows this read never saw.</param>
/// <param name="AnchorLost">Whether the previous position could not be found again.</param>
[UnsupportedOSPlatform("windows")]
internal sealed record PaneTextGridRead(
    PaneTextGridState State,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> CursorRows,
    bool LinesMissed,
    bool AnchorLost);

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
internal static class PaneTextGridReader
{
    private const int StableReadAttempts = 3;

    /// <summary>Reads what is on screen now, with no previous position.</summary>
    /// <param name="pane">The pane to read.</param>
    /// <param name="baselinePid">The pid the caller last saw, or null on a first read.</param>
    /// <param name="cancellationToken">Cancels the tmux queries.</param>
    /// <param name="rejectDeadAtEntry">Rejects an already-dead pane before capture.</param>
    /// <returns>The read.</returns>
    internal static async Task<PaneTextGridRead> ReadVisibleAsync(
        Pane pane,
        string? baselinePid,
        CancellationToken cancellationToken,
        bool rejectDeadAtEntry = false)
    {
        for (int attempt = 0; attempt < StableReadAttempts; attempt++)
        {
            PaneTextGridState before = await RequireStateAsync(pane, cancellationToken)
                .ConfigureAwait(false);
            if (rejectDeadAtEntry && baselinePid is null && before.Dead)
            {
                throw new PaneDeadAtEntryException(pane.Id);
            }

            IReadOnlyList<string> lines = await CaptureAsync(pane, null, cancellationToken)
                .ConfigureAwait(false);
            PaneTextGridState after = await RequireStateAsync(pane, cancellationToken)
                .ConfigureAwait(false);

            if (before == after)
            {
                if (baselinePid is not null
                    && !string.Equals(after.PanePid, baselinePid, StringComparison.Ordinal))
                {
                    throw new PaneReplacedException(pane.Id);
                }

                IReadOnlyList<string> cursorRows = CursorRowsFromCapture(lines, 0, after);
                return new PaneTextGridRead(after, lines, cursorRows, false, baselinePid is not null);
            }
        }

        throw new UnstableSnapshotException(pane.Id);
    }

    /// <summary>Reads what a pane has printed since a cursor was issued.</summary>
    /// <param name="pane">The pane to read.</param>
    /// <param name="cursor">Where the last read finished.</param>
    /// <param name="cancellationToken">Cancels the tmux queries.</param>
    /// <returns>The read.</returns>
    internal static async Task<PaneTextGridRead> ReadSinceAsync(
        Pane pane,
        IPaneTextGridCursor cursor,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < StableReadAttempts; attempt++)
        {
            PaneTextGridState before = await RequireStateAsync(pane, cancellationToken)
                .ConfigureAwait(false);
            RaiseIfPaneReplaced(pane, before, cursor);

            if (AnchorLost(cursor, before))
            {
                PaneTextGridRead missed = await ReadVisibleAsync(pane, cursor.PanePid, cancellationToken)
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

            PaneTextGridState after = await RequireStateAsync(pane, cancellationToken)
                .ConfigureAwait(false);
            RaiseIfPaneReplaced(pane, after, cursor);

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
                    PaneTextGridRead missed = await ReadVisibleAsync(pane, cursor.PanePid, cancellationToken)
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

            return new PaneTextGridRead(after, reported, cursorRows, false, false);
        }

        PaneTextGridRead busy = await ReadVisibleAsync(pane, cursor.PanePid, cancellationToken)
            .ConfigureAwait(false);
        return busy with { LinesMissed = true, AnchorLost = true };
    }

    internal sealed class UnstableSnapshotException : IOException
    {
        internal UnstableSnapshotException(PaneId paneId)
            : base($"Pane {paneId} changed during every snapshot attempt. Try again when "
                + "its output is less busy.")
        {
        }
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
        try
        {
            return await pane.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TmuxCommandException failure)
        {
            bool? stillExists = null;
            try
            {
                stillExists = (await pane.Server.GetPanesAsync(cancellationToken)
                    .ConfigureAwait(false)).Any(candidate => candidate.Id == pane.Id);
            }
            catch (LibTmuxException probeFailure)
            {
                failure.Data["LibTmux.PaneExistenceProbeFailure"] = probeFailure;
            }

            if (stillExists == false)
            {
                throw new PaneGoneException(pane.Id);
            }

            throw;
        }
    }

    private static IReadOnlyList<string> CursorRowsFromCapture(
        IReadOnlyList<string> rows,
        int captureStart,
        PaneTextGridState state)
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
        IPaneTextGridCursor cursor)
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

    private static async Task<PaneTextGridState> RequireStateAsync(
        Pane pane,
        CancellationToken cancellationToken)
    {
        PaneTextGridState? state = await PaneTextGridState.ReadAsync(pane, cancellationToken)
            .ConfigureAwait(false);
        return state ?? throw new PaneGoneException(pane.Id);
    }

    private static void RaiseIfPaneReplaced(Pane pane, PaneTextGridState state, IPaneTextGridCursor cursor)
    {
        if (!string.Equals(state.PanePid, cursor.PanePid, StringComparison.Ordinal))
        {
            throw new PaneReplacedException(pane.Id);
        }
    }

    private static bool AnchorLost(IPaneTextGridCursor cursor, PaneTextGridState state)
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

    private static bool TrimRisk(IPaneTextGridCursor cursor, PaneTextGridState state)
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
        IPaneTextGridCursor cursor,
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
                    PaneTextGridCursor.HashLine(rows[index]),
                    cursor.AnchorHash,
                    StringComparison.Ordinal)
                || (cursor.BelowCount > 0
                    && !string.Equals(
                        PaneTextGridCursor.HashRows(rows, index + 1, cursor.BelowCount),
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
        IPaneTextGridCursor cursor)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        List<string> kept = [];
        if (cursor.AnchorHash is null
            || !string.Equals(PaneTextGridCursor.HashLine(rows[0]), cursor.AnchorHash, StringComparison.Ordinal))
        {
            // The anchor row was rewritten since it was seen, so it is new text.
            kept.Add(rows[0]);
        }

        int index = 1;
        if (cursor.SuffixCount > 0
            && rows.Count - index >= cursor.SuffixCount
            && string.Equals(
                PaneTextGridCursor.HashRows(rows, index, cursor.SuffixCount),
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
            if (!PaneTextGridCursor.TrackedRowUnchanged(digests!, row, rows[index + row]))
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

    internal sealed class PaneGoneException(PaneId paneId)
        : IOException($"Pane {paneId} no longer has the process being observed.");

    internal sealed class PaneDeadAtEntryException(PaneId paneId)
        : IOException($"Pane {paneId} was already dead when reading began.");

    internal sealed class PaneReplacedException(PaneId paneId)
        : IOException($"Pane {paneId} is running a different process than when observation began.");
}
