using System.Runtime.Versioning;

namespace LibTmux.Examples.Snippets;

/// <summary>
/// One client held open, so tmux reports what nobody asked for.
/// </summary>
[UnsupportedOSPlatform("windows")]
public static class ControlMode
{
    /// <summary>Waits for a command's rendered ready line.</summary>
    [Example("Wait for text rendered by a pane")]
    public static async Task WaitForPaneText()
    {
        #region WaitForPaneText
        string? tmux = Environment.GetEnvironmentVariable("LIBTMUX_TMUX");
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions { TmuxBinaryPath = tmux ?? "tmux" });
        NewSessionRequest request = new()
        {
            Name = $"text-wait-{Guid.NewGuid():N}",
            Command = "exec /bin/sh",
        };
        await using OwnedSessionScope owned =
            await server.CreateOwnedSessionAsync(request);
        Window window = (await owned.Value.GetWindowsAsync()).Single();
        Pane pane = (await window.GetPanesAsync()).Single();

        PaneWaitRequest ready = PaneWaitRequest.FromTextPatterns(
            ["observer-ready"], simpleMatch: true);
        Task<PaneWaitResult> waiting = pane.WaitForTextAsync(
            ready with { Timeout = TimeSpan.FromSeconds(5) });
        await pane.SendTextAsync("printf 'observer-%s\\n' ready");
        PaneWaitResult result = await waiting;
        if (result.Outcome is not (PaneWaitOutcome.Matched
            or PaneWaitOutcome.PresentAtEntry))
        {
            throw new InvalidOperationException(
                $"Pane text wait ended: {result.Outcome}");
        }

        Console.WriteLine($"{result.Outcome}: {string.Join(' ', result.Tail)}");
        #endregion
    }

    /// <summary>Waits for tmux to announce the window this created.</summary>
    [Example("Hold a client open and read an event nobody asked for")]
    public static async Task WatchForWindowAdd(
        Server server,
        CancellationToken ct)
    {
        #region WatchForWindowAdd
        await using IControlModeSession control =
            await server.EnterControlModeAsync(cancellationToken: ct);

        TmuxCommand create =
            TmuxCommand.Create("new-window", "-d", "-n", "build");
        await control.SendAsync(create, ct);

        await foreach (TmuxEvent seen in control.Events.WithCancellation(ct))
        {
            if (seen is TmuxNotificationEvent { Name: "window-add" } added)
            {
                Console.WriteLine($"window-add {added.Arguments[0]}");
                break;
            }
        }
        #endregion
    }

    /// <summary>
    /// Reads the marker that says the event buffer discarded events.
    /// </summary>
    [Example("React to a control stream that fell behind")]
    public static async Task NoticeDroppedEvents(
        Server server,
        CancellationToken ct)
    {
        #region NoticeDroppedEvents
        await using IControlModeSession control =
            await server.EnterControlModeAsync(cancellationToken: ct);

        TmuxCommand create =
            TmuxCommand.Create("new-window", "-d", "-n", "build");
        await control.SendAsync(create, ct);

        await foreach (TmuxEvent seen in control.Events.WithCancellation(ct))
        {
            if (seen is TmuxEventsDroppedEvent dropped)
            {
                // Anything cached from this stream is now a guess, so the
                // marker is a signal to re-read rather than to log.
                Console.WriteLine(
                    $"missed {dropped.Count}, {dropped.TotalDropped} in total");
                continue;
            }

            if (seen is TmuxNotificationEvent { Name: "window-add" })
            {
                break;
            }
        }
        #endregion
    }
}
