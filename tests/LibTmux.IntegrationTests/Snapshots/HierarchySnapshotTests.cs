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
    public async Task Captured_fields_cannot_be_rewritten_through_the_public_dictionary()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Window window = Assert.Single(await server.GetWindowsAsync(token));
        string original = window.Name;
        var fields = Assert.IsAssignableFrom<IDictionary<string, string?>>(window.RawFormatFields);

        Assert.Throws<NotSupportedException>(() => fields["window_name"] = "rewritten");
        Assert.Equal(original, window.Name);
    }

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData("session")]
    [InlineData("window")]
    [InlineData("pane")]
    public async Task Typed_lookup_returns_readable_scalar_state(string kind)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);

        switch (kind)
        {
            case "session":
                Session session = await server.GetSessionAsync(new SessionId(0), token);
                Assert.Equal(raw.SessionName, session.Name);
                Assert.False(session.Attached);
                Assert.NotEmpty(session.RawFormatFields);
                break;
            case "window":
                Window window = await server.GetWindowAsync(new WindowId(0), token);
                Assert.NotEmpty(window.Name);
                Assert.Equal(80, window.Width);
                Assert.Equal(0, window.Index);
                Assert.Equal(new SessionId(0), window.EntityKey.SessionId);
                break;
            case "pane":
                Pane pane = await server.GetPaneAsync(new PaneId(0), token);
                Assert.Equal(80, pane.Width);
                Assert.Equal(0, pane.Index);
                Assert.True(pane.AtTop);
                Assert.True(pane.AtBottom);
                break;
        }
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Inactive_rows_leave_the_parents_active_relations_uncaptured()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Assert.Equal(0, (await raw.ExecuteAsync(
            ["new-window", "-d", "-t", "$0:", "-n", "inactive"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(
            ["split-window", "-d", "-t", "@1"], token)).ExitCode);

        Window inactive = (await server.GetWindowsAsync(token)).Single(window => window.Name == "inactive");
        Pane inactivePane = (await server.GetPanesAsync(token)).Single(pane => pane.Id == new PaneId(2));
        var activeWindow = Assert.IsType<CapturedRelation<Window>>(inactive.Session.ActiveWindow);
        var sessionPane = Assert.IsType<CapturedRelation<Pane>>(inactive.Session.ActivePane);
        var windowPane = Assert.IsType<CapturedRelation<Pane>>(inactivePane.Window.ActivePane);

        Assert.False(activeWindow.IsCaptured);
        Assert.False(sessionPane.IsCaptured);
        Assert.False(windowPane.IsCaptured);
        Assert.Equal(raw.SessionName, inactive.Session.Name);
        Assert.Equal("inactive", inactivePane.Window.Name);
        Assert.Equal(raw.SessionName, inactivePane.Session.Name);

        Server snapshot = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
        Pane capturedPane = snapshot.Panes.Single(pane => pane.Id == inactivePane.Id);
        Assert.True(capturedPane.Window.ActivePane.IsCaptured);
        Assert.True(capturedPane.Session.ActiveWindow.IsCaptured);
        Assert.True(snapshot.Sessions[0].ActiveWindow.Single().Panes.IsCaptured);
        Assert.Same(snapshot.Sessions[0], capturedPane.Window.Session);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Captured_relations_keep_scalar_state_after_the_daemon_stops()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Session session = Assert.Single(await server.GetSessionsAsync(token));
        Window window = Assert.Single(await server.GetWindowsAsync(token));
        Pane pane = Assert.Single(await server.GetPanesAsync(token));
        Assert.Equal(0, (await raw.ExecuteAsync(["kill-server"], token)).ExitCode);

        Assert.Equal(window.Name, session.ActiveWindow.Single().Name);
        Assert.Equal(pane.Width, session.ActivePane.Single().Width);
        Assert.Equal(pane.Height, window.ActivePane.Single().Height);
        Assert.Equal(raw.SessionName, window.Session.Name);
        Assert.Equal(raw.SessionName, pane.Session.Name);
        Assert.Equal(window.Name, pane.Window.Name);
    }

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
        Assert.All(snapshot.Sessions, session =>
            Assert.All(session.Panes, pane => Assert.Same(session, pane.Session)));
        Assert.All(snapshot.Windows, window =>
            Assert.All(window.Panes, pane => Assert.Same(window, pane.Window)));
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);
}
