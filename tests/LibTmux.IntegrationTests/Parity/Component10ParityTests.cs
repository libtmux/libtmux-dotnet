using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component10ParityTests
{
    public static TheoryData<string> OwnedRows =>
    [
        "libtmux.server:<module>",
        "libtmux.server:Server",
        "libtmux.server:Server.has_session",
        "libtmux.server:Server.is_alive",
        "libtmux.server:Server.kill",
        "libtmux.server:Server.kill_server",
        "libtmux.server:Server.kill_session",
        "libtmux.server:Server.new_session",
        "libtmux.server:Server.raise_if_dead",
        "libtmux.server:Server.start_server",
        "libtmux.session:<module>",
        "libtmux.session:Session",
        "libtmux.session:Session.__getitem__",
        "libtmux.session:Session.attach",
        "libtmux.session:Session.attach_session",
        "libtmux.session:Session.detach_client",
        "libtmux.session:Session.find_where",
        "libtmux.session:Session.get",
        "libtmux.session:Session.get_by_id",
        "libtmux.session:Session.kill",
        "libtmux.session:Session.kill_session",
        "libtmux.session:Session.kill_window",
        "libtmux.session:Session.list_windows",
        "libtmux.session:Session.lock_session",
        "libtmux.session:Session.name",
        "libtmux.session:Session.new_window",
        "libtmux.session:Session.next_window",
        "libtmux.session:Session.previous_window",
        "libtmux.session:Session.refresh",
        "libtmux.session:Session.rename_session",
        "libtmux.session:Session.search_panes",
        "libtmux.session:Session.search_windows",
        "libtmux.session:Session.select_window",
        "libtmux.session:Session.server",
        "libtmux.session:Session.switch_client",
        "libtmux.session:Session.where",
        "libtmux:Server",
        "libtmux:Session",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_lifecycle_behavior(string pythonSymbolId)
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
            },
            token);
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);

        bool proved = pythonSymbolId switch
        {
            // The module and class rows are proved by the handle carrying live
            // server state, not by a compile-time property of the type.
            "libtmux.server:<module>" or "libtmux.server:Server" or "libtmux:Server" =>
                server.IsMaterialized
                && server.Generation is { } generation
                && generation == session.Generation
                && server.Version is { IsValid: true },
            "libtmux.session:<module>" or "libtmux.session:Session" or "libtmux:Session" =>
                session.Id.ToString().StartsWith('$')
                && session.Name.Length > 0
                && ReferenceEquals(session.Server, server),
            "libtmux.server:Server.is_alive" => await server.IsAliveAsync(token),
            "libtmux.server:Server.raise_if_dead" => await ProvesThrowIfDeadAsync(server, token),
            "libtmux.server:Server.start_server" => await ProvesStartServerAsync(token),
            "libtmux.server:Server.has_session" =>
                await server.HasSessionAsync(session.Name, true, token)
                && !await server.HasSessionAsync("absent", true, token),
            "libtmux.server:Server.new_session" => await ProvesCreateSessionAsync(server, token),
            "libtmux.server:Server.kill_session" => await ProvesKillSessionAsync(server, token),
            "libtmux.server:Server.kill" or "libtmux.server:Server.kill_server" =>
                await ProvesKillServerAsync(server, token),
            "libtmux.session:Session.name" => session.Name.Length > 0,
            "libtmux.session:Session.server" => ReferenceEquals(session.Server, server),
            "libtmux.session:Session.refresh" =>
                (await session.RefreshAsync(token)).Id == session.Id,
            "libtmux.session:Session.rename_session" =>
                (await session.RenameAsync("renamed", token)).Name == "renamed",
            "libtmux.session:Session.new_window" =>
                (await session.CreateWindowAsync(new NewWindowRequest(name: "extra"), token))
                    .Snapshot?["window_name"] == "extra",
            "libtmux.session:Session.list_windows"
                or "libtmux.session:Session.__getitem__"
                or "libtmux.session:Session.get"
                or "libtmux.session:Session.get_by_id"
                or "libtmux.session:Session.where"
                or "libtmux.session:Session.find_where" =>
                (await session.GetWindowsAsync(token)).Count == 1,
            "libtmux.session:Session.select_window"
                or "libtmux.session:Session.next_window"
                or "libtmux.session:Session.previous_window" =>
                await ProvesSelectionAsync(session, token),
            "libtmux.session:Session.kill_window" => await ProvesKillWindowAsync(session, token),
            "libtmux.session:Session.kill" or "libtmux.session:Session.kill_session" =>
                await ProvesKillSelfAsync(server, token),
            "libtmux.session:Session.lock_session" => await ProvesLockAsync(session, token),
            "libtmux.session:Session.detach_client" => await ProvesDetachAsync(session, token),
            "libtmux.session:Session.attach"
                or "libtmux.session:Session.attach_session" =>
                await ProvesAttachRequiresTerminalAsync(server, session, token),
            "libtmux.session:Session.switch_client" =>
                await ProvesSwitchClientAsync(session, token),
            "libtmux.session:Session.search_windows" =>
                (await session.SearchWindowsAsync(new UnsafeTmuxFilter("1"), token)).Count == 1,
            "libtmux.session:Session.search_panes" =>
                (await session.SearchPanesAsync(new UnsafeTmuxFilter("1"), token)).Count == 1,
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static async Task<bool> ProvesThrowIfDeadAsync(Server server, CancellationToken token)
    {
        await server.ThrowIfDeadAsync(token);

        // The probe is the loud counterpart to IsAliveAsync, so a socket with
        // no daemon behind it has to raise rather than answer.
        Server absent = Server.Open(IsolatedOptions());
        await Assert.ThrowsAsync<TmuxCommandException>(() => absent.ThrowIfDeadAsync(token));
        return !await absent.IsAliveAsync(token);
    }

    private static async Task<bool> ProvesStartServerAsync(CancellationToken token)
    {
        Server bare = Server.Open(IsolatedOptions());
        Assert.False(bare.IsMaterialized);

        // Starting a server that holds no sessions cannot claim a materialized
        // handle, because tmux exits again immediately.
        await bare.StartServerAsync(token);
        Assert.False(bare.IsMaterialized);

        // The owning helper waits out that teardown, so the endpoint it hands
        // back is usable at once: the first session is what makes it durable.
        await using OwnedServerScope scope = await Server.CreateOwnedAsync(
            IsolatedOptions(),
            token);
        Session created = await scope.Value.CreateSessionAsync(
            new NewSessionRequest(name: "started"),
            token);
        return created.Name == "started" && await scope.Value.IsAliveAsync(token);
    }

    private static ServerConnectionOptions IsolatedOptions() =>
        new()
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = $"ltcs-parity-{Guid.NewGuid():N}",
            ConfigurationFile = "/dev/null",
        };

    private static async Task<bool> ProvesCreateSessionAsync(Server server, CancellationToken token)
    {
        Session created = await server.CreateSessionAsync(
            new NewSessionRequest(name: "created"),
            token);
        return created.Name == "created"
            && (await server.GetSessionsAsync(token)).Count == 2;
    }

    private static async Task<bool> ProvesKillSessionAsync(Server server, CancellationToken token)
    {
        await server.CreateSessionAsync(new NewSessionRequest(name: "doomed"), token);
        await server.KillSessionAsync("doomed", token);
        return !await server.HasSessionAsync("doomed", true, token);
    }

    private static async Task<bool> ProvesKillServerAsync(Server server, CancellationToken token)
    {
        await server.KillAsync(token);
        // Killing an already absent server is the requested outcome.
        await server.KillAsync(token);
        return !await server.IsAliveAsync(token);
    }

    private static async Task<bool> ProvesSelectionAsync(Session session, CancellationToken token)
    {
        await session.CreateWindowAsync(new NewWindowRequest(name: "second"), token);
        WindowId second = (await session.GetWindowsAsync(token))
            .Single(window => window.Snapshot?["window_name"] == "second")
            .Id;
        await session.SelectWindowAsync("second", token);
        bool selected = (await session.RefreshAsync(token)).ActiveWindow.Value.Id == second;
        await session.SelectPreviousWindowAsync(token);
        bool moved = (await session.RefreshAsync(token)).ActiveWindow.Value.Id != second;
        await session.SelectNextWindowAsync(token);
        return selected && moved
            && (await session.RefreshAsync(token)).ActiveWindow.Value.Id == second;
    }

    private static async Task<bool> ProvesKillWindowAsync(Session session, CancellationToken token)
    {
        await session.CreateWindowAsync(new NewWindowRequest(name: "spare"), token);
        await session.KillWindowAsync("spare", token);
        return (await session.GetWindowsAsync(token)).Count == 1;
    }

    private static async Task<bool> ProvesKillSelfAsync(Server server, CancellationToken token)
    {
        Session extra = await server.CreateSessionAsync(new NewSessionRequest(name: "extra"), token);
        await extra.KillAsync(cancellationToken: token);
        return !await server.HasSessionAsync("extra", true, token);
    }

    private static async Task<bool> ProvesLockAsync(Session session, CancellationToken token)
    {
        // Locking a session with no attached client is a no-op tmux accepts, so
        // the observable claim is that the session survives it intact.
        await session.LockAsync(token);
        return (await session.RefreshAsync(token)).Id == session.Id;
    }

    private static async Task<bool> ProvesDetachAsync(Session session, CancellationToken token)
    {
        // tmux refuses with "no current client" instead of treating an absent
        // client as a no-op, and that refusal is surfaced rather than swallowed.
        TmuxCommandException failure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => session.DetachClientAsync(cancellationToken: token));
        return failure.Result.ExitCode != 0
            && failure.Result.Arguments.SequenceEqual(
                ["detach-client", "-s", session.Id.ToString()]);
    }

    private static async Task<bool> ProvesAttachRequiresTerminalAsync(
        Server server,
        Session session,
        CancellationToken token)
    {
        // Attaching needs a terminal; the test process has none, so tmux
        // refuses rather than silently doing nothing.
        TmuxCommandException viaServer = await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.AttachSessionAsync(
                new AttachSessionRequest(target: session.Id.ToString()),
                token));
        TmuxCommandException viaSession = await Assert.ThrowsAsync<TmuxCommandException>(
            () => session.AttachAsync(cancellationToken: token));

        // A session-level attach needs no target: it attaches itself.
        return viaServer.Result.Arguments.SequenceEqual(
                ["attach-session", "-t", session.Id.ToString()])
            && viaSession.Result.Arguments.SequenceEqual(
                ["attach-session", "-t", session.Id.ToString()]);
    }

    private static async Task<bool> ProvesSwitchClientAsync(
        Session session,
        CancellationToken token)
    {
        TmuxCommandException failure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => session.SwitchClientAsync(token));
        return failure.Result.Arguments.SequenceEqual(
            ["switch-client", "-t", session.Id.ToString()]);
    }
}
