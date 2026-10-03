using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.ControlMode;

/// <summary>Keeps output floods from running beside timing-sensitive tests.</summary>
[CollectionDefinition("Output floods", DisableParallelization = true)]
public sealed class OutputFloodCollectionDefinition
{
}

// A flood keeps tmux and the reader busy for about a second, long enough to
// slow a shell another test is waiting on.
[UnsupportedOSPlatform("windows")]
[Collection("Output floods")]
public sealed class ControlModeFloodTests
{
    [UnixFact]
    public async Task A_flooding_pane_loses_only_output_and_is_paused_until_the_reader_catches_up()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
                ControlModeEventBufferCapacity = 16,
            },
            token);
        await using IControlModeSession control = await server.EnterControlModeAsync(raw.SessionName, token);
        string tmux = $"'{raw.TmuxBinaryPath}'";
        string flooded = raw.SessionName + "-flooded";

        // The marker window opens between two floods, and nothing reads the
        // stream until both are over.
        RawTmuxResult created = await raw.ExecuteAsync(
            [
                "new-window", "-d", "-P", "-F", "#{pane_id}", "-t", raw.SessionName,
                $"seq 1 100000; {tmux} new-window -d -n marker; seq 1 100000; {tmux} wait-for -S {flooded}; exec sleep 60",
            ],
            token);
        var flooding = PaneId.Parse(created.StandardOutputText.Trim());
        await raw.ExecuteAsync(["wait-for", flooded], token);
        RawTmuxResult marker = await raw.ExecuteAsync(
            ["list-windows", "-t", raw.SessionName, "-f", "#{==:#{window_name},marker}", "-F", "#{window_id}"],
            token);

        var losses = new List<TmuxEventsDroppedEvent>();
        bool markerAdded = false;
        bool paused = false;
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        await foreach (TmuxEvent item in control.Events.WithCancellation(watchdog.Token))
        {
            if (item is TmuxPaneContinuedEvent continued && continued.PaneId == flooding)
            {
                break;
            }

            losses.AddRange(item is TmuxEventsDroppedEvent loss ? [loss] : []);
            markerAdded |= item is TmuxNotificationEvent { Name: "window-add", Arguments: [string window] }
                && window == marker.StandardOutputText.Trim();
            paused |= item is TmuxPanePausedEvent pause && pause.PaneId == flooding;
        }

        Assert.NotEmpty(losses);
        Assert.All(losses, loss => Assert.True(loss.OnlyOutput));
        Assert.True(markerAdded);
        Assert.True(paused);
    }
}
