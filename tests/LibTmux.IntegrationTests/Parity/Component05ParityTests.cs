using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component05ParityTests
{
    public static TheoryData<string> OwnedRows =>
    [
        "libtmux.pane:Pane.server",
        "libtmux.pane:Pane.session",
        "libtmux.pane:Pane.window",
        "libtmux.session:Session._list_windows",
        "libtmux.session:Session._windows",
        "libtmux.session:Session.active_pane",
        "libtmux.session:Session.active_window",
        "libtmux.session:Session.attached_pane",
        "libtmux.session:Session.attached_window",
        "libtmux.session:Session.children",
        "libtmux.session:Session.panes",
        "libtmux.session:Session.windows",
        "libtmux.window:Window._list_panes",
        "libtmux.window:Window._panes",
        "libtmux.window:Window.active_pane",
        "libtmux.window:Window.attached_pane",
        "libtmux.window:Window.children",
        "libtmux.window:Window.linked_sessions",
        "libtmux.window:Window.panes",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_relation_behavior(string pythonSymbolId)
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
        Server snapshot = await server.CaptureSnapshotAsync(
            SnapshotDepth.Panes,
            TestContext.Current.CancellationToken);
        Session session = snapshot.Sessions[0];

        bool proved = pythonSymbolId switch
        {
            "libtmux.pane:Pane.server" => ReferenceEquals(
                (await session.GetPanesAsync(TestContext.Current.CancellationToken))[0].Server,
                server),
            "libtmux.pane:Pane.session" =>
                (await session.GetPanesAsync(TestContext.Current.CancellationToken))[0]
                    .Session.Id == session.Id,
            "libtmux.pane:Pane.window" =>
                (await session.GetPanesAsync(TestContext.Current.CancellationToken))[0]
                    .Window.Id == session.ActiveWindow.Id,
            "libtmux.session:Session.windows"
                or "libtmux.session:Session._windows"
                or "libtmux.session:Session._list_windows"
                or "libtmux.session:Session.children" =>
                (await session.GetWindowsAsync(TestContext.Current.CancellationToken)).Count == 1,
            "libtmux.session:Session.panes" =>
                (await session.GetPanesAsync(TestContext.Current.CancellationToken)).Count == 1,
            "libtmux.session:Session.active_window"
                or "libtmux.session:Session.attached_window" =>
                session.ActiveWindow.Id
                    == (await session.GetWindowsAsync(TestContext.Current.CancellationToken))[0].Id,
            "libtmux.session:Session.active_pane"
                or "libtmux.session:Session.attached_pane" =>
                session.ActivePane.Id
                    == (await session.GetPanesAsync(TestContext.Current.CancellationToken))[0].Id,
            "libtmux.window:Window.panes"
                or "libtmux.window:Window._panes"
                or "libtmux.window:Window._list_panes"
                or "libtmux.window:Window.children" =>
                (await (await session.GetWindowsAsync(TestContext.Current.CancellationToken))[0]
                    .GetPanesAsync(TestContext.Current.CancellationToken)).Count == 1,
            "libtmux.window:Window.active_pane"
                or "libtmux.window:Window.attached_pane" =>
                (await session.GetWindowsAsync(TestContext.Current.CancellationToken))[0]
                    .ActivePane.Id == session.ActivePane.Id,
            "libtmux.window:Window.linked_sessions" =>
                await ProvesLinkedSessionsAsync(raw, session),
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static async Task<bool> ProvesLinkedSessionsAsync(
        RawTmuxTestContext raw,
        Session session)
    {
        Window window =
            (await session.GetWindowsAsync(TestContext.Current.CancellationToken))[0];
        await raw.ExecuteAsync(
            ["new-session", "-d", "-s", "linked"],
            TestContext.Current.CancellationToken);
        await raw.ExecuteAsync(
            ["link-window", "-s", window.Id.ToString(), "-t", "linked:"],
            TestContext.Current.CancellationToken);

        IReadOnlyList<Session> linked =
            await window.GetLinkedSessionsAsync(TestContext.Current.CancellationToken);

        // One window, two sessions: linkage is a set, not a single parent.
        return linked.Count == 2
            && linked.Select(static entry => entry.Id).Distinct().Count() == 2;
    }
}
