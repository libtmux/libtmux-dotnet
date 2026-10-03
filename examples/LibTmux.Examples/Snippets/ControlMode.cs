using System.Runtime.Versioning;

namespace LibTmux.Examples.Snippets;

/// <summary>One client held open, so tmux reports what nobody asked for.</summary>
[UnsupportedOSPlatform("windows")]
public static class ControlMode
{
    /// <summary>Waits for a command's rendered ready line.</summary>
    [Example("Wait for text rendered by a pane")]
    public static async Task WaitForPaneText()
    {
        #region WaitForPaneText
        Server server = await Server.ConnectAsync(new ServerConnectionOptions
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
        });
        await using OwnedSessionScope owned = await server.CreateOwnedSessionAsync(
            new NewSessionRequest
            {
                Name = $"text-wait-{Guid.NewGuid():N}",
                Command = "exec /bin/sh",
            });
        Window window = (await owned.Value.GetWindowsAsync()).Single();
        Pane pane = (await window.GetPanesAsync()).Single();

        await using PaneTextObserver observer = new();
        Task<PaneTextWaitResult> waiting = observer.WaitForTextAsync(
            pane,
            new PaneTextWaitRequest
            {
                Patterns = ["observer-ready"],
                SimpleMatch = true,
                Timeout = TimeSpan.FromSeconds(5),
            });
        await pane.SendTextAsync("printf 'observer-%s\\n' ready");
        PaneTextWaitResult result = await waiting;
        if (result.Outcome is not (PaneTextWaitOutcome.Matched
            or PaneTextWaitOutcome.PresentAtEntry))
        {
            throw new InvalidOperationException($"Pane text wait ended: {result.Outcome}");
        }

        Console.WriteLine($"{result.Outcome}: {string.Join(' ', result.Tail)}");
        #endregion
    }

    /// <summary>Waits for tmux to announce the window this created.</summary>
    [Example("Hold a client open and read an event nobody asked for")]
    public static async Task WatchForWindowAdd(Server server, CancellationToken ct)
    {
        #region WatchForWindowAdd
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: ct);

        await control.SendAsync(TmuxCommand.Create("new-window", "-d", "-n", "build"), ct);

        await foreach (TmuxEvent observed in control.Events.WithCancellation(ct))
        {
            if (observed is TmuxNotificationEvent { Name: "window-add" } added)
            {
                Console.WriteLine($"window-add {added.Arguments[0]}");
                break;
            }
        }
        #endregion
    }

    /// <summary>Reads the marker that says the event buffer discarded events.</summary>
    [Example("React to a control stream that fell behind")]
    public static async Task NoticeDroppedEvents(Server server, CancellationToken ct)
    {
        #region NoticeDroppedEvents
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: ct);

        await control.SendAsync(TmuxCommand.Create("new-window", "-d", "-n", "build"), ct);

        await foreach (TmuxEvent observed in control.Events.WithCancellation(ct))
        {
            if (observed is TmuxEventsDroppedEvent dropped)
            {
                // Anything cached from this stream is now a guess, so the
                // marker is a signal to re-read rather than to log.
                Console.WriteLine($"missed {dropped.Count}, {dropped.TotalDropped} in total");
                continue;
            }

            if (observed is TmuxNotificationEvent { Name: "window-add" })
            {
                break;
            }
        }
        #endregion
    }
}
