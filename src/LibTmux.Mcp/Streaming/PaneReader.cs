using System.Runtime.Versioning;
using LibTmux.Internal;
using ModelContextProtocol;

namespace LibTmux.Mcp;

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

/// <summary>Adapts shared grid reads to MCP cursor and error contracts.</summary>
[UnsupportedOSPlatform("windows")]
internal static class PaneReader
{
    internal static async Task<PaneRead> ReadVisibleAsync(
        Pane pane,
        string? baselinePid,
        CancellationToken cancellationToken)
    {
        try
        {
            return FromCore(await PaneTextGridReader.ReadVisibleAsync(
                    pane, baselinePid, cancellationToken, rejectDeadAtEntry: true)
                .ConfigureAwait(false));
        }
        catch (PaneTextGridReader.PaneDeadAtEntryException)
        {
            throw new McpException(
                $"Pane {pane.Id} is dead: the program in it has exited. "
                + "Use respawn_pane to start it again.");
        }
        catch (PaneTextGridReader.UnstableSnapshotException error)
        {
            throw new UnstableSnapshotException(pane.Id, error);
        }
        catch (PaneTextGridReader.PaneReplacedException)
        {
            throw ReplacementError(pane.Id);
        }
        catch (PaneTextGridReader.PaneGoneException)
        {
            throw new McpException(
                $"tmux did not report the state of pane {pane.Id}. It may have just closed.");
        }
    }

    internal static async Task<PaneRead> ReadSinceAsync(
        Pane pane,
        TailCursor cursor,
        CancellationToken cancellationToken)
    {
        try
        {
            return FromCore(await PaneTextGridReader.ReadSinceAsync(
                    pane, cursor, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (PaneTextGridReader.UnstableSnapshotException error)
        {
            throw new UnstableSnapshotException(pane.Id, error);
        }
        catch (PaneTextGridReader.PaneReplacedException)
        {
            throw ReplacementError(pane.Id);
        }
        catch (PaneTextGridReader.PaneGoneException)
        {
            throw new McpException(
                $"tmux did not report the state of pane {pane.Id}. It may have just closed.");
        }
    }

    internal static Task<IReadOnlyList<string>> CaptureAsync(
        Pane pane,
        int? start,
        CancellationToken cancellationToken) =>
        PaneTextGridReader.CaptureAsync(pane, start, cancellationToken);

    internal static int? FindUniqueAnchor(
        IReadOnlyList<string> rows,
        TailCursor cursor,
        CancellationToken cancellationToken) =>
        PaneTextGridReader.FindUniqueAnchor(rows, cursor, cancellationToken);

    internal static List<string> DropAlreadySeen(
        IReadOnlyList<string> rows,
        TailCursor cursor) => PaneTextGridReader.DropAlreadySeen(rows, cursor);

    internal sealed class UnstableSnapshotException : McpException
    {
        internal UnstableSnapshotException(PaneId paneId, Exception inner)
            : base($"Pane {paneId} changed during every snapshot attempt. Try again when "
                + "its output is less busy.", inner)
        {
        }
    }

    private static PaneRead FromCore(PaneTextGridRead read) => new(
        PaneGridState.FromCore(read.State),
        read.Lines,
        read.CursorRows,
        read.LinesMissed,
        read.AnchorLost);

    private static McpException ReplacementError(PaneId paneId) => new(
        $"Pane {paneId} is running a different process than when the cursor was "
        + "issued, so there is nothing to continue from. Call capture_since "
        + "again without a cursor.");
}
