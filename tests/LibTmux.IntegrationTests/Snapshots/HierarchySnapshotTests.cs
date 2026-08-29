using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Snapshots;

[UnsupportedOSPlatform("windows")]
public sealed class HierarchySnapshotTests
{
    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Linked_windows_preserve_edges_without_losing_entity_identity()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        var options = new ServerConnectionOptions(
            tmuxBinaryPath: raw.TmuxBinaryPath,
            socketPath: raw.SocketPath,
            configurationFile: "/dev/null");
        Server server = await Server.ConnectAsync(
            options,
            TestContext.Current.CancellationToken);
        await raw.ExecuteAsync(
            ["new-session", "-d", "-s", "target"],
            TestContext.Current.CancellationToken);
        RawTmuxResult source = await raw.ExecuteAsync(
            ["list-windows", "-a", "-F", "#{session_name}\t#{window_id}"],
            TestContext.Current.CancellationToken);
        string windowId = source.StandardOutputLines[0].Split('\t')[1];
        await raw.ExecuteAsync(
            ["link-window", "-s", windowId, "-t", "target:"],
            TestContext.Current.CancellationToken);

        Server snapshot = await server.CaptureSnapshotAsync(
            SnapshotDepth.Panes,
            TestContext.Current.CancellationToken);

        SessionWindowEdge[] linked =
        [
            .. snapshot.Windows.Select(static window => window.Edge).Where(
                edge => edge.WindowId.ToString() == windowId),
        ];

        // The same window is linked into two sessions, so it must appear once
        // per session while remaining one window identity.
        Assert.Equal(2, linked.Length);
        Assert.Single(linked.Select(static edge => edge.WindowId).Distinct());
        Assert.Equal(2, linked.Select(static edge => edge.SessionId).Distinct().Count());
        Assert.True(snapshot.Panes.IsCaptured);
    }
}
