using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux.IntegrationTests.Clients;

[UnsupportedOSPlatform("windows")]
public sealed class ClientAdministrationTests
{
    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Detached_client_resolves_nullable_attachment()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // A server nobody is attached to has no clients, and that is the
        // ordinary case rather than a failure.
        Assert.Empty(await server.GetClientsAsync(token));
        await Assert.ThrowsAsync<TmuxObjectNotFoundException>(
            () => Client.GetAsync(server, "/dev/null", token));

        await using PtyAttachedClientScope attached = await PtyAttachedClientScope.StartAsync(
            raw,
            token);

        Client client = await WaitForClientAsync(server, token);
        Assert.Equal(attached.Tty, client.Tty);
        Assert.False(client.IsControlClient);
        Assert.NotNull(client.AttachedSessionId);
        Assert.Equal(server.Generation, client.Generation);
        Assert.Same(server, client.Server);

        // The three parts come from one reading, so they agree with each other.
        ClientAttachment? live = await client.ResolveAttachmentAsync(token);
        Assert.NotNull(live);
        Assert.Equal(client.AttachedSessionId, live.Session!.Id);
        Assert.NotNull(live.Window);
        Assert.NotNull(live.Pane);
        Assert.Equal(live.Session.Id, (await client.GetAttachedSessionAsync(token))!.Id);
        Assert.Equal(live.Window!.Id, (await client.GetAttachedWindowAsync(token))!.Id);
        Assert.Equal(live.Pane!.Id, (await client.GetAttachedPaneAsync(token))!.Id);

        Client byName = await Client.GetAsync(server, client.Name, token);
        Assert.Equal(client, byName);
        Assert.Equal(client.Name, (await client.RefreshAsync(token)).Name);

        // A second client proves the detach names one rather than clearing the
        // server, and leaves the first scope its own client to dispose.
        await using (PtyAttachedClientScope second = await PtyAttachedClientScope.StartAsync(
            raw,
            token))
        {
            Client other = await WaitForClientCountAsync(server, 2, token);
            await server.DetachClientAsync(other.Name, cancellationToken: token);
            await WaitForClientCountAsync(server, 1, token);

            // The detached handle still says what it read, and the resolving
            // call says there is nothing left to resolve.
            Assert.NotNull(other.AttachedSessionId);
            Assert.Null(await other.ResolveAttachmentAsync(token));
            Assert.Null(await other.GetAttachedSessionAsync(token));
            Assert.Null(await other.GetAttachedWindowAsync(token));
            Assert.Null(await other.GetAttachedPaneAsync(token));
            await Assert.ThrowsAsync<TmuxObjectNotFoundException>(() => other.RefreshAsync(token));
        }
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Control_client_reads_back_as_one()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        await using ControlModeClientScope control = await ControlModeClientScope.StartAsync(
            raw,
            token);

        // The only assertion in the suite a broken read cannot satisfy: every
        // other client test attaches a terminal, for which false is the right
        // answer whether or not the field resolves.
        Client client = await WaitForClientAsync(server, token);
        Assert.Equal(control.ClientName, client.Name);
        Assert.True(client.IsControlClient);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task Attach_switch_detach_lock_and_suspend_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        Session other = await server.CreateSessionAsync(new NewSessionRequest(name: "other"), token);

        // A server-level attach has no session to fall back to, and attaching
        // needs a terminal the test process does not have.
        await Assert.ThrowsAsync<ArgumentException>(
            () => server.AttachSessionAsync(new AttachSessionRequest(), token));
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.AttachSessionAsync(
                new AttachSessionRequest(target: other.Id.ToString()),
                token));

        // With no client of its own, every client-scoped command is refused by
        // tmux rather than quietly doing nothing.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.SwitchClientAsync(other.Id.ToString(), token));
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.LockClientAsync(cancellationToken: token));
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.SuspendClientAsync(cancellationToken: token));

        await using PtyAttachedClientScope attached = await PtyAttachedClientScope.StartAsync(
            raw,
            token);
        Client client = await WaitForClientAsync(server, token);

        await server.RefreshClientAsync(client.Name, cancellationToken: token);
        await server.SwitchClientAsync(other.Id.ToString(), token);

        // Switching moved the client, which is visible in a fresh reading.
        Client moved = await client.RefreshAsync(token);
        Assert.Equal(other.Id, moved.AttachedSessionId);

        // Keeping a client is spelled with the same target flag as detaching
        // one, so the only difference is the all-but flag. It always spares
        // one, which is why naming none detaches nothing from here.
        await server.DetachAllClientsAsync(client.Name, cancellationToken: token);
        Assert.Contains(
            await server.GetClientsAsync(token),
            candidate => candidate.Name == client.Name);

        await server.DetachAllClientsAsync(cancellationToken: token);
        Assert.Contains(
            await server.GetClientsAsync(token),
            candidate => candidate.Name == client.Name);

        // Locking runs lock-command in the client's terminal and tmux stops
        // listing the client until it unlocks, so this goes last: everything
        // above needs a client tmux will still name.
        await server.LockClientAsync(client.Name, token);
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task RefreshClientClipboardQueryVersionPolicy()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingLogger logger = new();
        Server server = await ConnectAsync(raw, token, logger);
        await using PtyAttachedClientScope attached = await PtyAttachedClientScope.StartAsync(
            raw,
            token);
        Client client = await WaitForClientAsync(server, token);

        bool supported = TmuxCapabilities.GetRequired(server.Version!.Value)
            .Capabilities.Contains("refresh_client_clipboard_query");

        await server.RefreshClientAsync(client.Name, requestClipboard: true, cancellationToken: token);

        if (supported)
        {
            // tmux 3.7 carries the flag, so nothing had to be dropped.
            Assert.Empty(logger.Warnings);
        }
        else
        {
            // Older tmux has no way to ask, so the flag is omitted and the
            // redraw still happens in one command.
            Assert.Single(logger.Warnings);
        }

        // Without the request the flag never appears, on any lane.
        logger.Clear();
        await server.RefreshClientAsync(client.Name, cancellationToken: token);
        Assert.Empty(logger.Warnings);
    }

    private static Task<Server> ConnectAsync(
        RawTmuxTestContext raw,
        CancellationToken token,
        ILogger? logger = null) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null",
                logger: logger),
            token);

    private static async Task<Client> WaitForClientAsync(Server server, CancellationToken token)
    {
        // Attaching is asynchronous on tmux's side, so the client appears a
        // moment after the process starts.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            IReadOnlyList<Client> clients = await server.GetClientsAsync(token);
            if (clients.Count > 0)
            {
                return clients[0];
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        }

        throw new InvalidOperationException("tmux never reported an attached client.");
    }

    private static async Task<Client> WaitForClientCountAsync(
        Server server,
        int expected,
        CancellationToken token)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        IReadOnlyList<Client> clients = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            clients = await server.GetClientsAsync(token);
            if (clients.Count == expected)
            {
                return clients[^1];
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        }

        throw new InvalidOperationException(
            $"tmux reports {clients.Count} clients, expected {expected}.");
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public void Clear() => _warnings.Clear();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            // The dispatcher records every command failure at error level, and
            // these proofs are about the warning a dropped flag produces, so
            // only warnings are counted.
            if (logLevel == LogLevel.Warning)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }
}
