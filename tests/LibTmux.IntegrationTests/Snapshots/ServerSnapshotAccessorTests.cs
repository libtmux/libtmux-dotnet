using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Testing;

namespace LibTmux.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class ServerSnapshotAccessorTests
{
    [UnixFact]
    public async Task Reading_a_capture_never_reaches_tmux()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxTestOptions options = HarnessOptions();
        await using TemporaryServerScope scope = await factory.CreateServerAsync(options, token);
        await using TemporarySessionScope one = await factory.CreateSessionAsync(
            scope.Server,
            options,
            token);
        await using TemporarySessionScope two = await factory.CreateSessionAsync(
            scope.Server,
            options,
            token);

        // A scope hands back the endpoint it started, which has not discovered
        // a server yet. Capturing off it is the obvious call, so it is the one
        // that has to work.
        Server live = scope.Server;
        Assert.False(live.IsMaterialized);

        // A handle that has captured nothing says so, rather than answering an
        // empty list a caller would read as "there are none".
        Assert.False(live.Sessions.IsCaptured);

        Server captured = await live.CaptureSnapshotAsync(
            SnapshotDepth.Sessions,
            token);
        Assert.True(captured.Sessions.IsCaptured);
        Assert.Equal(2, captured.Sessions.Count);

        // Killing a session leaves the capture saying what it found, because a
        // capture is a reading rather than a live view.
        await one.Session.KillAsync(cancellationToken: token);
        Assert.Equal(2, captured.Sessions.Count);
        Assert.Single(await captured.GetSessionsAsync(token));

        // The handle that captured is a new one, so the handle already held
        // still answers what it did before.
        Assert.False(live.Sessions.IsCaptured);
    }

    [UnixFact]
    public async Task A_shallow_capture_says_what_it_did_not_read()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxTestOptions options = HarnessOptions();
        await using TemporaryServerScope scope = await factory.CreateServerAsync(options, token);
        await using TemporarySessionScope session = await factory.CreateSessionAsync(
            scope.Server,
            options,
            token);

        Server live = session.Session.Server;
        Server shallow = await live.CaptureSnapshotAsync(SnapshotDepth.Server, token);
        Assert.False(shallow.Sessions.IsCaptured);

        // Clients are attached to a session rather than contained by one, so a
        // hierarchy capture never holds them however deep it goes.
        Server deep = await live.CaptureSnapshotAsync(SnapshotDepth.Panes, token);
        Assert.True(deep.Sessions.IsCaptured);
        Assert.False(deep.Clients.IsCaptured);
        Assert.True(deep.Windows.IsCaptured);

        // A capture that stopped at sessions has looked for windows and found
        // none to record, which is not the same as never having looked.
        Server sessionsOnly = await live.CaptureSnapshotAsync(SnapshotDepth.Sessions, token);
        Assert.True(sessionsOnly.Sessions.IsCaptured);
        Assert.False(sessionsOnly.Windows.IsCaptured);
        Assert.False(sessionsOnly.Sessions[0].Windows.IsCaptured);
    }

    [UnixFact]
    public async Task Walking_a_capture_down_and_back_up_lands_on_the_same_handles()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        TmuxTestOptions options = HarnessOptions();
        await using TemporaryServerScope scope = await factory.CreateServerAsync(options, token);
        await using TemporarySessionScope session = await factory.CreateSessionAsync(
            scope.Server,
            options,
            token);
        await session.Session.CreateWindowAsync(new NewWindowRequest(), token);

        Server captured = await session.Session.Server.CaptureSnapshotAsync(
            SnapshotDepth.Panes,
            token);
        Session first = captured.Sessions[0];
        Assert.Equal(2, first.Windows.Count);
        Assert.NotEmpty(first.Panes);

        Window window = first.Windows[0];
        Assert.NotEmpty(window.Panes);
        Assert.Equal(first.Id, window.EntityKey.SessionId);
        Assert.Equal(window.Id, window.Edge.WindowId);
        Assert.Equal(0, window.Edge.Ordinal);

        // Coming back up reaches the session that was walked down from, with
        // its windows still on it rather than a second, emptier copy.
        Session linked = Assert.Single(window.LinkedSessions);
        Assert.Equal(first, linked);
        Assert.True(linked.Windows.IsCaptured);
        Assert.Equal(first.Windows.Count, linked.Windows.Count);
    }

    private static TmuxTestOptions HarnessOptions() =>
        new(new ServerConnectionOptions { TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux", SocketName = $"lts-{Guid.NewGuid():N}"[..20], ConfigurationFile = "/dev/null" });
}
