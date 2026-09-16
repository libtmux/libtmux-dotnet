using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace LibTmux;

/// <summary>The pane a stream was watching left its window's arrangement.</summary>
/// <param name="PaneId">The pane that is gone.</param>
/// <remarks>
/// tmux's control protocol has no notification naming a killed pane
/// specifically, only a general <c>%layout-change</c> or window-close for the
/// window it was in. This is synthesized by <see cref="PaneObservation" />
/// once it confirms, by asking tmux directly, that the watched pane no longer
/// resolves - after any output already buffered for it has been delivered.
/// </remarks>
public sealed record TmuxPaneGoneEvent(PaneId PaneId) : TmuxEvent;

/// <summary>Narrows a control client's event stream to one pane, and ends it cleanly.</summary>
/// <remarks>
/// A killed pane's session and window usually survive it, so the general
/// event stream just keeps running - the closest thing tmux sends is a
/// <c>%layout-change</c> for the window the pane left. Watching a pane
/// through this instead answers "is the pane I care about still there" as a
/// typed, terminal event rather than leaving a caller to infer it from a
/// notification shaped like any other.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public static class PaneObservation
{
    private static readonly string[] ArrangementChangeNames =
        ["layout-change", "window-close", "unlinked-window-close"];

    /// <summary>Watches one pane's output until it ends.</summary>
    /// <param name="session">The control client to watch through.</param>
    /// <param name="pane">The pane to watch.</param>
    /// <param name="cancellationToken">Stops watching.</param>
    /// <returns>
    /// The pane's own output, ending with <see cref="TmuxExitEvent" /> when the
    /// control client itself ended, or with <see cref="TmuxPaneGoneEvent" />
    /// once the pane is confirmed gone. It never hangs: every arrangement
    /// change that might mean the pane left is checked as it arrives.
    /// </returns>
    [UnsupportedOSPlatform("windows")]
    public static async IAsyncEnumerable<TmuxEvent> WatchAsync(
        this IControlModeSession session,
        Pane pane,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pane);
        PaneId paneId = pane.Id;

        await foreach (TmuxEvent candidate in session.Events.WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            switch (candidate)
            {
                case TmuxOutputEvent output when output.PaneId == paneId:
                    yield return output;
                    break;

                case TmuxExitEvent exit:
                    yield return exit;
                    yield break;

                case TmuxNotificationEvent notification
                    when Array.IndexOf(ArrangementChangeNames, notification.Name) >= 0:
                    if (!await StillResolvesAsync(session, paneId, cancellationToken).ConfigureAwait(false))
                    {
                        yield return new TmuxPaneGoneEvent(paneId);
                        yield break;
                    }

                    break;

                default:
                    break;
            }
        }
    }

    // display-message's target is CMD_FIND_CANFAIL: tmux never errors on one
    // it cannot resolve, it answers with an empty line instead, so absence is
    // read from the reply's content rather than from a thrown exception.
    [UnsupportedOSPlatform("windows")]
    private static async Task<bool> StillResolvesAsync(
        IControlModeSession session,
        PaneId paneId,
        CancellationToken cancellationToken)
    {
        string target = paneId.ToString();
        try
        {
            IReadOnlyList<string> reply = await session.SendAsync(
                    TmuxCommand.Create("display-message", "-p", "-t", target, "#{pane_id}"),
                    cancellationToken)
                .ConfigureAwait(false);
            return reply.Count > 0 && reply[0] == target;
        }
        catch (ControlModeCommandException)
        {
            return false;
        }
    }
}
