using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Snapshots;

[UnsupportedOSPlatform("windows")]
public sealed class ServerMirrorTests
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    [UnixFact]
    public async Task An_announced_change_is_published_as_a_newer_view()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Session anchor = await AnchorAsync(raw, token);
        await using ServerMirror mirror = await ServerMirror.OpenAsync(anchor, cancellationToken: token);

        await raw.ExecuteAsync(["new-window", "-d", "-n", "added", "-t", raw.SessionName], token);
        ServerMirrorView added = await mirror.WaitUntilAsync(
            view => view.Server.Windows.Any(window => window.Name == "added"),
            Arrival,
            token);

        Assert.True(added.Epoch > 0);
        Assert.Contains(added.Clients, client => client.IsControlClient);
    }

    [UnixFact]
    public async Task A_change_tmux_does_not_announce_is_seen_within_the_refresh_interval()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Session anchor = await AnchorAsync(raw, token);
        await using ServerMirror mirror = await ServerMirror.OpenAsync(
            anchor,
            TimeSpan.FromMilliseconds(100),
            token);

        // A pane's running command changes without any notification.
        await raw.ExecuteAsync(["respawn-pane", "-k", "-t", $"{raw.SessionName}:0.0", "exec sleep 60"], token);
        ServerMirrorView running = await mirror.WaitUntilAsync(
            view => view.Server.Panes.Any(pane => pane.CurrentCommand == "sleep"),
            Arrival,
            token);

        Assert.True(running.Epoch > 0);
    }

    // With detach-on-destroy off, tmux moves the mirror's client to another
    // session instead of ending it.
    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData("on")]
    [InlineData("off")]
    public async Task The_mirror_ends_when_its_anchor_session_is_gone(string detachOnDestroy)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        await raw.ExecuteAsync(["set-option", "-g", "detach-on-destroy", detachOnDestroy], token);
        await raw.ExecuteAsync(["new-session", "-d", "-s", raw.SessionName + "-other"], token);
        Session anchor = await AnchorAsync(raw, token);
        await using ServerMirror mirror = await ServerMirror.OpenAsync(anchor, cancellationToken: token);

        await raw.ExecuteAsync(["kill-session", "-t", raw.SessionName], token);
        ServerMirrorView? view = mirror.Current;
        while (view is not null)
        {
            view = await mirror.WaitForNewerAsync(view.Epoch, token).WaitAsync(Arrival, token);
        }

        Assert.True(mirror.IsEnded);
        Assert.IsType<TmuxObjectNotFoundException>(mirror.Failure);
    }

    // Killed without %exit, the client's stream faults while the server and
    // the anchor live on, so the mirror attaches a new client.
    [UnixFact]
    public async Task The_mirror_reattaches_when_its_client_is_killed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Session anchor = await AnchorAsync(raw, token);
        await using ServerMirror mirror = await ServerMirror.OpenAsync(anchor, cancellationToken: token);
        string[] killed = System.Text.Encoding.UTF8.GetString(
            (await raw.ExecuteAsync(["list-clients", "-F", "#{client_pid} #{client_name}"], token)).StandardOutput).Trim().Split(' ', 2);

        using (System.Diagnostics.Process client = System.Diagnostics.Process.GetProcessById(int.Parse(killed[0], System.Globalization.CultureInfo.InvariantCulture)))
        {
            client.Kill();
            await client.WaitForExitAsync(token);
        }

        await raw.ExecuteAsync(["new-window", "-d", "-n", "after", "-t", raw.SessionName], token);
        ServerMirrorView seen = await mirror.WaitUntilAsync(
            view => view.Server.Windows.Any(window => window.Name == "after")
                && view.Clients.Any(client => client.IsControlClient),
            Arrival,
            token);

        Assert.False(mirror.IsEnded);
        Assert.DoesNotContain(seen.Clients, client => client.Name == killed[1]);
    }

    // A waiter whose condition never holds must fail when the server dies
    // under a refreshing mirror, not sleep out its own timeout.
    [UnixFact]
    public async Task A_waiter_fails_when_the_server_dies_under_a_refreshing_mirror()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Session anchor = await AnchorAsync(raw, token);
        await using ServerMirror mirror = await ServerMirror.OpenAsync(
            anchor,
            TimeSpan.FromMilliseconds(50),
            token);
        Task<ServerMirrorView> waiting = mirror.WaitUntilAsync(_ => false, TimeSpan.FromMinutes(10), token);

        await raw.ExecuteAsync(["kill-server"], token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => waiting.WaitAsync(Arrival, token));
        Assert.True(mirror.IsEnded);
    }

    private static async Task<Session> AnchorAsync(RawTmuxTestContext raw, CancellationToken token)
    {
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
            },
            token);
        IReadOnlyList<Session> sessions = await server.GetSessionsAsync(token);
        return Assert.Single(sessions, session => session.Name == raw.SessionName);
    }
}
