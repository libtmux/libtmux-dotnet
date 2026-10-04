using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>The pane a stream was watching left its window's arrangement.</summary>
/// <param name="PaneId">The pane that is gone.</param>
/// <remarks>
/// tmux's control protocol has no notification naming a killed pane
/// specifically, only a general <c>%layout-change</c> or window-close for the
/// window it was in. This is synthesized by <see cref="PaneObservation" />
/// once it confirms, by asking tmux directly, that the watched pane no longer
/// resolves - after output buffered by a watermark-backed control session has
/// been delivered.
/// </remarks>
public sealed record TmuxPaneGoneEvent(PaneId PaneId) : TmuxEvent;

/// <summary>Narrows a control client's event stream to some panes, and ends it cleanly.</summary>
/// <remarks>
/// A killed pane's session and window usually survive it, so the general
/// event stream just keeps running - the closest thing tmux sends is a
/// <c>%layout-change</c> for the window the pane left. Watching panes
/// through this instead answers "is the pane I care about still there" as a
/// typed, terminal event rather than leaving a caller to infer it from a
/// notification shaped like any other.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public static class PaneObservation
{
    private static readonly string[] ArrangementChangeNames =
        ["layout-change", "window-close", "unlinked-window-close", "session-changed"];

    /// <summary>Watches one pane's output until it ends.</summary>
    /// <param name="session">The control client to watch through.</param>
    /// <param name="pane">The pane to watch.</param>
    /// <param name="cancellationToken">Stops watching.</param>
    /// <returns>
    /// The pane's own output, with <see cref="TmuxPanePausedEvent" /> and
    /// <see cref="TmuxPaneContinuedEvent" /> around output a slow reader missed,
    /// ending with <see cref="TmuxExitEvent" /> when the
    /// control client itself ended, or with <see cref="TmuxPaneGoneEvent" />
    /// once the pane is confirmed gone. Loss reports require resynchronization;
    /// buffered output precedes pane termination for watermark-backed control
    /// sessions. The client remains borrowed.
    /// </returns>
    /// <remarks>
    /// tmux discards output it has not yet sent to a control client once the
    /// pane's program exits, so the last lines of a program that exits at once
    /// may never arrive. Read final output with
    /// <see cref="Pane.RunAsync(string, TimeSpan, CancellationToken)" />, or
    /// capture a pane kept with <c>remain-on-exit</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// When reading starts, the pane is in a session other than the one the
    /// client is attached to; tmux sends a control client output only from its
    /// own session.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// While reading, the pane's window left the client's session, so tmux
    /// sends none of its output any more.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A control session without an event watermark cannot establish which
    /// output was buffered before pane termination. Its watch fails when the
    /// pane is confirmed gone, without consuming another event.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public static IAsyncEnumerable<TmuxEvent> WatchAsync(
        this IControlModeSession session,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pane);
        return WatchCoreAsync(session, [pane], cancellationToken);
    }

    /// <summary>Watches several panes' output through one control client until each has ended.</summary>
    /// <param name="session">The control client to watch through.</param>
    /// <param name="panes">The panes to watch; at least one.</param>
    /// <param name="cancellationToken">Stops watching.</param>
    /// <returns>
    /// The panes' own output in the order tmux sent it, with
    /// <see cref="TmuxPanePausedEvent" /> and <see cref="TmuxPaneContinuedEvent" />
    /// around output a slow reader missed. Each pane confirmed gone is reported
    /// by a <see cref="TmuxPaneGoneEvent" /> after the output buffered before
    /// it went, unless the client ends first; panes found gone together are
    /// reported in the order given. The stream ends once every pane is gone,
    /// or with <see cref="TmuxExitEvent" /> when the control client itself
    /// ended. The client remains borrowed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A client has one event stream, so this is how one client serves
    /// several panes: each output event names its pane. Events after the last
    /// pane's end stay unread for the client's next reader. Each layout change,
    /// window close, session change or loss report lists the client's session's
    /// panes in one command, and the server's panes in a second when a watched
    /// pane is missing from it.
    /// </para>
    /// <para>
    /// tmux discards output it has not yet sent to a control client once a
    /// pane's program exits, so the last lines of a program that exits at once
    /// may never arrive. Read final output with
    /// <see cref="Pane.RunAsync(string, TimeSpan, CancellationToken)" />, or
    /// capture a pane kept with <c>remain-on-exit</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="panes" /> is empty, or, when reading starts, a pane is in
    /// a session other than the one the client is attached to; tmux sends a
    /// control client output only from its own session.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// While reading, a pane's window left the client's session, so tmux sends
    /// none of its output any more.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A control session without an event watermark cannot establish which
    /// output was buffered before a pane ended. Its watch fails when a pane is
    /// confirmed gone, without consuming another event.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public static IAsyncEnumerable<TmuxEvent> WatchAsync(
        this IControlModeSession session,
        IReadOnlyCollection<Pane> panes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(panes);
        List<Pane> distinct = [];
        HashSet<PaneId> seen = [];
        foreach (Pane pane in panes)
        {
            ArgumentNullException.ThrowIfNull(pane, nameof(panes));
            if (seen.Add(pane.Id))
            {
                distinct.Add(pane);
            }
        }

        if (distinct.Count == 0)
        {
            throw new ArgumentException("Name at least one pane to watch.", nameof(panes));
        }

        return WatchCoreAsync(session, distinct, cancellationToken);
    }

    [UnsupportedOSPlatform("windows")]
    private static async IAsyncEnumerable<TmuxEvent> WatchCoreAsync(
        IControlModeSession session,
        IReadOnlyList<Pane> panes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Copied per enumeration: each one removes panes as they end.
        List<Pane> alive = [.. panes];
        HashSet<PaneId> watched = [.. alive.Select(pane => pane.Id)];

        // Panes confirmed gone, each with the last event written before it
        // went; panes found together keep the order they were given in.
        PriorityQueue<PaneId, (long Watermark, int Found)> ending = new();
        int found = 0;
        using CancellationTokenSource reading =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IControlModeEventWatermarkSource? source = session as IControlModeEventWatermarkSource;
        ControlModeEventBuffer.Reader? watermarked = source?.CreateEventReader(reading.Token);
        IAsyncEnumerator<TmuxEvent>? events = null;
        Exception? failure = null;
        try
        {
            await CheckForGoneAsync(starting: true).ConfigureAwait(false);
            if (watermarked is null)
            {
                events = session.Events.GetAsyncEnumerator(reading.Token);
            }

            while (true)
            {
                ControlModeEventRead read = await ReadAsync().ConfigureAwait(false);
                if (read == ControlModeEventRead.Boundary)
                {
                    // Everything buffered before these panes went has been read.
                    long boundary = ending.TryPeek(out _, out (long Watermark, int) first) ? first.Watermark : long.MaxValue;
                    while (ending.TryPeek(out PaneId gone, out (long Watermark, int) next) && next.Watermark <= boundary)
                    {
                        ending.Dequeue();
                        yield return new TmuxPaneGoneEvent(gone);
                    }

                    if (alive.Count == 0 && ending.Count == 0)
                    {
                        yield break;
                    }

                    continue;
                }

                if (read == ControlModeEventRead.Completed)
                {
                    yield break;
                }

                TmuxEvent current = watermarked?.Current ?? events!.Current;
                switch (current)
                {
                    case TmuxOutputEvent output when watched.Contains(output.PaneId):
                        yield return output;
                        break;

                    case TmuxPanePausedEvent paused when watched.Contains(paused.PaneId):
                        yield return paused;
                        break;

                    case TmuxPaneContinuedEvent continued when watched.Contains(continued.PaneId):
                        yield return continued;
                        break;

                    case TmuxEventsDroppedEvent loss:
                        yield return loss;
                        await CheckForGoneAsync(starting: false).ConfigureAwait(false);

                        break;

                    case TmuxExitEvent exit:
                        yield return exit;
                        yield break;

                    case TmuxNotificationEvent notification
                        when Array.IndexOf(ArrangementChangeNames, notification.Name) >= 0:
                        await CheckForGoneAsync(starting: false).ConfigureAwait(false);

                        break;
                }
            }
        }
        finally
        {
            try
            {
                if (watermarked is not null)
                {
                    await watermarked.DisposeAsync().ConfigureAwait(false);
                }
                else if (events is not null)
                {
                    await events.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                failure.Data["LibTmux.ControlModeCleanupFailure"] = cleanupFailure;
            }
        }

        // tmux sends a control client output only from panes in the session
        // it is attached to, so a watched pane elsewhere would stay silent
        // rather than end: one there when reading starts is refused, and one
        // whose window leaves later ends the watch. One listing of that
        // session answers for every watched pane still in it; the server's
        // listing then tells a pane that is gone from one that is elsewhere.
        async Task CheckForGoneAsync(bool starting)
        {
            Pane[] watched = [.. alive];
            if (watched.Length == 0)
            {
                return;
            }

            // Panes from different server generations are checked one by one,
            // so a stale one still fails as stale.
            bool oneGeneration = watched.All(pane => pane.Generation == watched[0].Generation);
            IReadOnlySet<string>? attached = oneGeneration
                ? await ListAsync(watched[0].Generation, "-s").ConfigureAwait(false)
                : null;
            if (attached is not null && watched.All(pane => attached.Contains(pane.Id.ToString())))
            {
                return;
            }

            IReadOnlySet<string>? listed = oneGeneration
                ? await ListAsync(watched[0].Generation, "-a").ConfigureAwait(false)
                : null;
            Pane? elsewhere = null;
            foreach (Pane pane in watched)
            {
                if (attached?.Contains(pane.Id.ToString()) == true)
                {
                    continue;
                }

                if (listed?.Contains(pane.Id.ToString()) ?? await CheckAsync(pane).ConfigureAwait(false))
                {
                    elsewhere ??= attached is null ? null : pane;
                    continue;
                }

                if (source is null)
                {
                    var error = new NotSupportedException(
                        "The control session cannot prove which events were buffered before the pane disappeared.");
                    failure = error;
                    throw error;
                }

                alive.Remove(pane);
                ending.Enqueue(pane.Id, (source.CaptureEventWatermark(), found++));
            }

            if (elsewhere is not null)
            {
                Exception error = starting
                    ? new ArgumentException(
                        $"Pane {elsewhere.Id} is not in the session this control client is attached to, and tmux "
                        + "sends a control client output only from that session. Watch it through a client entered "
                        + "on its session.")
                    : new InvalidOperationException(
                        $"Pane {elsewhere.Id} left the session this control client is attached to, and tmux sends "
                        + "a control client output only from that session. Watch it through a client entered on "
                        + "its new session.");
                failure = error;
                throw error;
            }
        }

        // -a lists every pane of the server; -s the panes of the client's own session.
        async Task<IReadOnlySet<string>?> ListAsync(ServerGeneration generation, string scope)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<string> reply = await session.SendAsync(
                        TmuxCommand.Create("list-panes", scope, "-F", "#{pane_id}") with
                        {
                            RequiredGeneration = generation,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                return reply.ToHashSet(StringComparer.Ordinal);
            }
            catch (InvalidOperationException error) when (
                error is not StaleServerGenerationException && !session.IsRunning)
            {
                // The client is ending, and its exit ends the watch; every
                // pane counts as present until then, as one by one.
                return alive.Select(pane => pane.Id.ToString()).ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
        }

        async Task<bool> CheckAsync(Pane pane)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await StillResolvesAsync(session, pane, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException error) when (
                error is not StaleServerGenerationException && !session.IsRunning)
            {
                return true;
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
        }

        async ValueTask<ControlModeEventRead> ReadAsync()
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reading.IsCancellationRequested)
                {
                    return ControlModeEventRead.Completed;
                }

                if (watermarked is not null)
                {
                    return ending.TryPeek(out _, out (long Watermark, int) next)
                        ? await watermarked.MoveNextThroughAsync(next.Watermark).ConfigureAwait(false)
                        : await watermarked.MoveNextAsync().ConfigureAwait(false);
                }

                return await events!.MoveNextAsync().ConfigureAwait(false)
                    ? ControlModeEventRead.Item
                    : ControlModeEventRead.Completed;
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
        }
    }

    // display-message's target is CMD_FIND_CANFAIL: tmux never errors on one
    // it cannot resolve, it answers with an empty line instead, so absence is
    // read from the reply's content rather than from a thrown exception.
    [UnsupportedOSPlatform("windows")]
    private static async Task<bool> StillResolvesAsync(
        IControlModeSession session,
        Pane pane,
        CancellationToken cancellationToken)
    {
        string target = pane.Id.ToString();
        try
        {
            IReadOnlyList<string> reply = await session.SendAsync(
                    TmuxCommand.Create("display-message", "-p", "-t", target, "#{pane_id}") with
                    {
                        RequiredGeneration = pane.Generation,
                    },
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
