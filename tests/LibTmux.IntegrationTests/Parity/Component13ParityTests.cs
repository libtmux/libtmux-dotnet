using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component13ParityTests
{
    public static TheoryData<string> OwnedRows =>
    [
        "libtmux.client:<module>",
        "libtmux.client:Client",
        "libtmux.client:Client.attached_pane",
        "libtmux.client:Client.attached_session",
        "libtmux.client:Client.attached_window",
        "libtmux.client:Client.from_client_name",
        "libtmux.client:Client.refresh",
        "libtmux.client:Client.server",
        "libtmux.server:Server.attach_session",
        "libtmux.server:Server.clients",
        "libtmux.server:Server.detach_all_clients",
        "libtmux.server:Server.detach_client",
        "libtmux.server:Server.list_clients",
        "libtmux.server:Server.lock_client",
        "libtmux.server:Server.refresh_client",
        "libtmux.server:Server.suspend_client",
        "libtmux.server:Server.switch_client",
        "libtmux:Client",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_client_behavior(string pythonSymbolId)
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);

        bool proved = pythonSymbolId switch
        {
            "libtmux.client:<module>" or "libtmux.client:Client" or "libtmux:Client" =>
                await ProvesIdentityAsync(raw, server, token),
            "libtmux.client:Client.server" =>
                await WithClientAsync(
                    raw,
                    server,
                    client => Task.FromResult(ReferenceEquals(client.Server, server)),
                    token),
            "libtmux.client:Client.attached_session" =>
                await ProvesAttachedSessionAsync(raw, server, token),
            "libtmux.client:Client.attached_window" =>
                await ProvesAttachedWindowAsync(raw, server, token),
            "libtmux.client:Client.attached_pane" =>
                await ProvesAttachedPaneAsync(raw, server, token),
            "libtmux.client:Client.from_client_name" =>
                await ProvesLookupByNameAsync(raw, server, token),
            "libtmux.client:Client.refresh" => await ProvesRefreshAsync(raw, server, token),
            "libtmux.server:Server.attach_session" => await ProvesAttachAsync(server, token),
            "libtmux.server:Server.clients" or "libtmux.server:Server.list_clients" =>
                await ProvesListingAsync(raw, server, token),
            "libtmux.server:Server.detach_client" => await ProvesDetachAsync(raw, server, token),
            "libtmux.server:Server.detach_all_clients" =>
                await ProvesDetachAllAsync(raw, server, token),
            "libtmux.server:Server.lock_client" => await ProvesLockAsync(raw, server, token),
            "libtmux.server:Server.refresh_client" => await ProvesRedrawAsync(raw, server, token),
            "libtmux.server:Server.suspend_client" => await ProvesSuspendAsync(raw, server, token),
            "libtmux.server:Server.switch_client" => await ProvesSwitchAsync(raw, server, token),
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static async Task<bool> ProvesIdentityAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token) =>
        await WithClientAsync(raw, server, client =>
        {
            // A client is named by its terminal and belongs to one reading of
            // one server, which is what makes two handles comparable.
            Assert.Equal(client.Tty, client.Name);
            Assert.False(client.IsControlClient);
            Assert.Equal(server.Generation, client.Generation);
            Assert.NotEmpty(client.RawFormatFields);
            return Task.FromResult(client.AttachedSessionId is not null);
        }, token);

    private static async Task<bool> ProvesAttachedSessionAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token) =>
        await WithClientAsync(raw, server, async client =>
        {
            Session? session = await client.GetAttachedSessionAsync(token);
            return session is not null && session.Id == client.AttachedSessionId;
        }, token);

    private static async Task<bool> ProvesAttachedWindowAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token) =>
        await WithClientAsync(raw, server, async client =>
        {
            Window? window = await client.GetAttachedWindowAsync(token);
            Session? session = await client.GetAttachedSessionAsync(token);
            return window is not null
                && session is not null
                && (await session.GetWindowsAsync(token)).Any(
                    candidate => candidate.Id == window.Id);
        }, token);

    private static async Task<bool> ProvesAttachedPaneAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token) =>
        await WithClientAsync(raw, server, async client =>
        {
            ClientAttachment? attachment = await client.ResolveAttachmentAsync(token);
            return attachment?.Pane is not null
                && attachment.Window is not null
                && (await attachment.Window.GetPanesAsync(token)).Any(
                    candidate => candidate.Id == attachment.Pane.Id);
        }, token);

    private static async Task<bool> ProvesLookupByNameAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token) =>
        await WithClientAsync(raw, server, async client =>
        {
            Client found = await Client.GetAsync(server, client.Name, token);
            await Assert.ThrowsAsync<TmuxObjectNotFoundException>(
                () => Client.GetAsync(server, "/dev/nowhere", token));
            return found.Equals(client);
        }, token);

    private static async Task<bool> ProvesRefreshAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token) =>
        await WithClientAsync(raw, server, async client =>
        {
            // Refreshing is worth having only because a client moves, so the
            // proof is that the replacement disagrees with the original.
            Session elsewhere = await server.CreateSessionAsync(
                new NewSessionRequest(name: "refreshed"),
                token);
            await server.SwitchClientAsync(elsewhere.Id.ToString(), token);
            Client current = await client.RefreshAsync(token);
            return current.Name == client.Name
                && current.AttachedSessionId == elsewhere.Id
                && client.AttachedSessionId != elsewhere.Id;
        }, token);

    private static async Task<bool> ProvesAttachAsync(Server server, CancellationToken token)
    {
        // A server-level attach has no session of its own to fall back on, and
        // the test process has no terminal to attach.
        await Assert.ThrowsAsync<ArgumentException>(
            () => server.AttachSessionAsync(new AttachSessionRequest(), token));
        Session session = await TestHierarchy.RequireFirstSessionAsync(server, token);
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.AttachSessionAsync(
                new AttachSessionRequest(target: session.Id.ToString()),
                token));
        return true;
    }

    private static async Task<bool> ProvesListingAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token)
    {
        // Nobody attached is the ordinary case, not a failure to list.
        Assert.Empty(await server.GetClientsAsync(token));
        return await WithClientAsync(
            raw,
            server,
            async client => (await server.GetClientsAsync(token)).Count == 1
                && client.Tty is not null,
            token);
    }

    private static async Task<bool> ProvesDetachAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token) =>
        await WithClientAsync(raw, server, async client =>
        {
            await server.DetachClientAsync(client.Name, cancellationToken: token);
            return await WaitForClientCountAsync(server, 0, token);
        }, token);

    private static async Task<bool> ProvesDetachAllAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token) =>
        await WithClientAsync(raw, server, async client =>
        {
            // The all-but form always spares the client it is given, so the
            // only client on the server survives being named.
            await server.DetachAllClientsAsync(client.Name, cancellationToken: token);
            return (await server.GetClientsAsync(token)).Any(
                candidate => candidate.Name == client.Name);
        }, token);

    private static async Task<bool> ProvesLockAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token)
    {
        // With no client of its own the command is refused rather than ignored.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.LockClientAsync(cancellationToken: token));

        return await WithClientAsync(
            raw,
            server,
            async client =>
            {
                // Locking runs tmux's lock-command, which may not exist on this
                // machine, so this only proves the request is accepted, not survival.
                await server.LockClientAsync(client.Name, token);
                return true;
            },
            token);
    }

    private static async Task<bool> ProvesRedrawAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token) =>
        await WithClientAsync(raw, server, async client =>
        {
            await server.RefreshClientAsync(client.Name, cancellationToken: token);
            await server.RefreshClientAsync(client.Name, requestClipboard: true, cancellationToken: token);
            return (await server.GetClientsAsync(token)).Any(
                candidate => candidate.Name == client.Name);
        }, token);

    private static async Task<bool> ProvesSuspendAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token)
    {
        // With no client of its own the command is refused rather than ignored.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.SuspendClientAsync(cancellationToken: token));
        return await WithClientAsync(
            raw,
            server,
            async client =>
            {
                RawTmuxResult listed = await raw.ExecuteAsync(
                    ["list-clients", "-F", "#{client_pid}"],
                    token);
                int clientProcessId = int.Parse(
                    listed.StandardOutputLines[0],
                    CultureInfo.InvariantCulture);

                await server.SuspendClientAsync(client.Name, token);

                // Unlike detach, suspend keeps the client process alive to be
                // resumed; when tmux drops it from the client list is unasserted.
                return IsRunning(clientProcessId);
            },
            token);
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            return !Process.GetProcessById(processId).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> ProvesSwitchAsync(
        RawTmuxTestContext raw,
        Server server,
        CancellationToken token) =>
        await WithClientAsync(raw, server, async client =>
        {
            Session elsewhere = await server.CreateSessionAsync(
                new NewSessionRequest(name: "switched"),
                token);
            await server.SwitchClientAsync(elsewhere.Id.ToString(), token);
            Session? landed = await client.GetAttachedSessionAsync(token);
            return landed?.Id == elsewhere.Id;
        }, token);

    private static async Task<bool> WithClientAsync(
        RawTmuxTestContext raw,
        Server server,
        Func<Client, Task<bool>> proof,
        CancellationToken token)
    {
        await using PtyAttachedClientScope attached = await PtyAttachedClientScope.StartAsync(
            raw,
            token);
        return await proof(await WaitForClientAsync(server, token));
    }

    private static async Task<Client> WaitForClientAsync(Server server, CancellationToken token)
    {
        // Attaching is asynchronous on tmux's side, so the client appears a
        // moment after the process starts.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestBudget.Settle;
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

    private static async Task<bool> WaitForClientCountAsync(
        Server server,
        int expected,
        CancellationToken token)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestBudget.Settle;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await server.GetClientsAsync(token)).Count == expected)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        }

        return false;
    }
}
