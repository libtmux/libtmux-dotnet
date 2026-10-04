using System.Runtime.Versioning;
using LibTmux.Internal;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <summary>Reads panes for tools, naming the tool that fixes each failure.</summary>
/// <remarks>
/// A read failure is raised as <see cref="McpException" />, not as a tmux
/// exception, because nothing was dispatched that could have acted: a tmux
/// exception would make a mutating tool warn that it may have.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class McpPaneReader
{
    internal static Task<PaneRead> ReadVisibleAsync(
        Pane pane,
        string? baselinePid,
        CancellationToken cancellationToken) =>
        PaneReader.ReadVisibleAsync(pane, baselinePid, Failure, cancellationToken);

    internal static Task<PaneRead> ReadSinceAsync(
        Pane pane,
        PaneCursor cursor,
        CancellationToken cancellationToken) =>
        PaneReader.ReadSinceAsync(pane, cursor, Failure, cancellationToken);

    internal static McpException Failure(PaneReadFailure failure, Pane pane) =>
        failure == PaneReadFailure.Unstable
            ? new UnstableSnapshotException(pane.Id)
            : new McpException(failure switch
            {
                PaneReadFailure.Dead => $"Pane {pane.Id} is dead: the program in it has exited. "
                    + "Use respawn_pane to start it again.",
                PaneReadFailure.Replaced => $"Pane {pane.Id} is running a different process than when the cursor was "
                    + "issued, so there is nothing to continue from. Call capture_since "
                    + "again without a cursor.",
                _ => $"tmux did not report the state of pane {pane.Id}. It may have just closed.",
            });

    internal sealed class UnstableSnapshotException(PaneId paneId)
        : McpException($"Pane {paneId} changed during every snapshot attempt. Try again when "
            + "its output is less busy.");
}
