using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Snapshots;

[UnsupportedOSPlatform("windows")]
public sealed class HierarchySnapshotTests
{
    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(SnapshotDepth.Windows)]
    [InlineData(SnapshotDepth.Panes)]
    public async Task Repeated_window_links_in_one_session_preserve_each_placement(
        SnapshotDepth depth)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        await raw.ExecuteAsync(["link-window", "-s", "@0", "-t", $"{raw.SessionName}:7"], token);
        await raw.ExecuteAsync(["select-window", "-t", $"{raw.SessionName}:7"], token);

        Server snapshot = await server.CaptureSnapshotAsync(depth, token);
        Session session = Assert.Single(snapshot.Sessions);
        Window[] windows = [.. session.Windows.OrderBy(window => window.Index)];
        Assert.Equal([0, 7], windows.Select(window => window.Index));
        Assert.All(windows, window => Assert.Equal(window.Index, window.Edge.WindowIndex));
        Assert.Equal(7, session.ActiveWindow.Value.Index);
        Assert.All(windows, window => Assert.Single(window.LinkedSessions));
        if (depth == SnapshotDepth.Panes)
        {
            Assert.All(windows, window =>
            {
                Pane pane = Assert.Single(window.Panes);
                Assert.Same(window, pane.Window);
                Assert.Equal(window.Index, pane.Window.Index);
            });
            Assert.Equal(7, session.ActivePane.Value.Window.Index);
        }
    }

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
        var activeWindow = Assert.IsType<CapturedValue<Window>>(inactive.Session.ActiveWindow);
        var sessionPane = Assert.IsType<CapturedValue<Pane>>(inactive.Session.ActivePane);
        var windowPane = Assert.IsType<CapturedValue<Pane>>(inactivePane.Window.ActivePane);

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
        Assert.True(snapshot.Sessions[0].ActiveWindow.Value.Panes.IsCaptured);
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

        Assert.Equal(window.Name, session.ActiveWindow.Value.Name);
        Assert.Equal(pane.Width, session.ActivePane.Value.Width);
        Assert.Equal(pane.Height, window.ActivePane.Value.Height);
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
        var options = new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null",
        };
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
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
            },
            token);
    [UnixFact]
    public async Task Repeated_window_links_preserve_each_placement_and_unique_linked_sessions()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null"
            },
            token);
        Assert.Equal(0, (await raw.ExecuteAsync(
            ["link-window", "-d", "-s", "$0:0", "-t", "$0:5"], token)).ExitCode);
        RawTmuxResult native = await raw.ExecuteAsync(
            ["list-windows", "-t", "$0", "-F", "#{window_index}"], token);
        Assert.Equal(["0", "5"], native.StandardOutputLines);
        Assert.Equal(0, (await raw.ExecuteAsync(["split-window", "-d", "-t", "%0"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["select-window", "-t", "$0:5"], token)).ExitCode);

        Server snapshot = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
        Window[] placements = [.. snapshot.Windows];
        Assert.Equal(2, placements.Length);
        Assert.Multiple(
            () => Assert.Equal([0, 5], placements.Select(window => window.Edge.WindowIndex)),
            () => Assert.Equal<int?>([0, 1], placements.Select(window => window.Edge.Ordinal)),
            () => Assert.All(placements, window => Assert.Single(window.LinkedSessions)),
            () => Assert.NotEqual(placements[0].EntityKey, placements[1].EntityKey));
        Assert.Equal([0, 5], Assert.Single(snapshot.Sessions).Windows.Select(window => window.Index));
        Assert.All(placements, window => Assert.Equal(window.EntityKey, window.Edge.Key));
        Assert.Equal(placements[0], placements[1]);
        Assert.True(placements[0] == placements[1]);
        Assert.Equal(placements[0].GetHashCode(), placements[1].GetHashCode());
        Assert.All(placements, window => Assert.Equal(2, window.Panes.Count));
        Assert.All(placements, window => Assert.All(window.Panes, pane => Assert.Equal(
            window.RawFormatFields["window_index"], pane.RawFormatFields["window_index"])));
        Assert.Single(await placements[1].GetLinkedSessionsAsync(token));

        await raw.DisposeAsync();
        Session capturedSession = Assert.Single(snapshot.Sessions);
        Assert.Multiple(
            () => Assert.Same(snapshot, capturedSession.Server),
            () => Assert.All(placements, window => Assert.Same(snapshot, window.Server)),
            () => Assert.All(placements, window => Assert.Same(capturedSession, window.Session)),
            () => Assert.All(placements, window => Assert.All(window.Panes, pane =>
                Assert.Same(window, pane.Window))),
            () => Assert.All(snapshot.Panes, pane => Assert.Same(capturedSession, pane.Session)),
            () => Assert.All(snapshot.Panes, pane => Assert.Same(snapshot, pane.Server)),
            () => Assert.Same(placements[1], capturedSession.ActiveWindow.Value),
            () => Assert.All(placements, window => Assert.Same(
                window.Panes.Single(pane => pane.Id == window.ActivePane.Value.Id), window.ActivePane.Value)),
            () => Assert.Same(placements[1].ActivePane.Value, capturedSession.ActivePane.Value));
        Pane selected = snapshot.Panes.First(pane => pane.Window.Index == 5);
        Assert.Equal(2, selected.Window.Panes.Count);
        Assert.Equal(2, selected.Session.Windows.Count);
        Assert.Equal(4, selected.Server.Panes.Count);
    }

    [UnixFact]
    public async Task Recapture_and_refresh_preserve_separate_graph_observations()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await Server.ConnectAsync(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null"
        }, token);
        Server first = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
        Session session = Assert.Single(first.Sessions);
        Window window = Assert.Single(first.Windows);
        Pane pane = Assert.Single(first.Panes);
        string oldName = window.Name;
        Assert.Equal(0, (await raw.ExecuteAsync(["rename-window", "-t", "@0", "graph-renamed"], token)).ExitCode);

        Window refreshedWindow = await window.RefreshAsync(token);
        Pane refreshedPane = await pane.RefreshAsync(token);
        Session refreshedSession = await session.RefreshAsync(token);
        Assert.NotSame(window, refreshedWindow);
        Assert.Equal("graph-renamed", refreshedWindow.Name);
        Assert.False(refreshedWindow.Panes.IsCaptured);
        Assert.False(refreshedWindow.Session.Windows.IsCaptured);
        Assert.False(refreshedPane.Window.Panes.IsCaptured);
        Assert.False(refreshedPane.Session.Windows.IsCaptured);
        Assert.False(refreshedSession.Windows.IsCaptured);
        Assert.Equal("graph-renamed", refreshedPane.Window.Name);

        Server second = await first.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
        await raw.DisposeAsync();
        Assert.Multiple(
            () => Assert.Same(first, window.Server),
            () => Assert.Same(first, refreshedWindow.Server),
            () => Assert.Same(first, refreshedPane.Server),
            () => Assert.Same(first, refreshedSession.Server),
            () => Assert.Same(second, Assert.Single(second.Windows).Server),
            () => Assert.Same(Assert.Single(second.Windows), Assert.Single(second.Panes).Window),
            () => Assert.Same(window, pane.Window),
            () => Assert.Equal(oldName, window.Name),
            () => Assert.Equal("graph-renamed", Assert.Single(second.Windows).Name));
    }

}
