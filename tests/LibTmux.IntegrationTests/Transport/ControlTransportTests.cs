using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;

namespace LibTmux.IntegrationTests.Transport;

[UnsupportedOSPlatform("windows")]
public sealed class ControlTransportTests
{
    [UnixFact]
    public async Task One_client_carries_the_commands_and_is_left_out_of_every_listing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Server sameSocket = await ConnectAsync(raw, token);

        Session session = Assert.Single(await server.GetSessionsAsync(token));
        _ = await sameSocket.GetSessionsAsync(token);

        // Both handles share the one client, which sizes nothing and hears no output.
        string[] own = await ControlClientsAsync(raw, token);
        string row = Assert.Single(own);
        Assert.Contains("ignore-size", row, StringComparison.Ordinal);
        Assert.Contains("no-output", row, StringComparison.Ordinal);

        // tmux counts it as attached; the library's own answers do not.
        Assert.Empty(await server.GetClientsAsync(token));
        Assert.Empty(await server.GetAttachedSessionsAsync(token));
        Assert.False(session.Attached);
        Assert.False((await session.RefreshAsync(token)).Attached);
        Assert.Equal(
            "1",
            (await raw.ExecuteAsync(["display-message", "-p", "-t", session.Id.ToString(), "#{session_attached}"], token))
                .StandardOutputText.Trim());
    }

    [UnixFact]
    public async Task A_client_command_with_no_client_named_fails_rather_than_acting_on_the_library_client()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await server.GetSessionsAsync(token);
        string before = Assert.Single(await ControlClientsAsync(raw, token));

        TmuxCommandException failure = await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.DetachClientAsync(cancellationToken: token));
        Assert.Contains("no current client", failure.Result.StandardErrorLines);
        Assert.Contains("no current client", (await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.LockClientAsync(cancellationToken: token))).Result.StandardErrorLines);
        _ = await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.DetachClientAsync(shellCommand: "true", cancellationToken: token));

        // Its client is still the one it was.
        _ = await server.GetSessionsAsync(token);
        Assert.Equal(before, Assert.Single(await ControlClientsAsync(raw, token)));
    }

    [UnixFact]
    public async Task A_client_command_with_no_client_named_acts_on_the_client_that_is_not_the_librarys()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await server.GetSessionsAsync(token);
        string before = Assert.Single(await ControlClientsAsync(raw, token));

        await using PtyAttachedClientScope attached = await PtyAttachedClientScope.StartAsync(raw, token);
        _ = await WaitForClientsAsync(server, 1, token);

        // Its commands make it the client tmux saw last, which is the one a
        // client command with no target would otherwise pick.
        _ = await server.GetSessionsAsync(token);
        await server.DetachClientAsync(cancellationToken: token);

        // The terminal client left; the library's did not.
        _ = await WaitForClientsAsync(server, 0, token);
        _ = await server.GetSessionsAsync(token);
        Assert.Equal(before, Assert.Single(await ControlClientsAsync(raw, token)));
    }

    [UnixFact]
    public async Task The_client_follows_its_server_when_its_session_ends()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await server.CreateSessionAsync(new NewSessionRequest { Name = "spare" }, token);
        IReadOnlyList<Session> sessions = await server.GetSessionsAsync(token);
        string pinned = SessionOf(Assert.Single(await ControlClientsAsync(raw, token)));

        await sessions.Single(session => session.Name == pinned).KillAsync(cancellationToken: token);

        // The next command meets a server whose client went with its session.
        Session remaining = Assert.Single(await server.GetSessionsAsync(token));
        Assert.NotEqual(pinned, remaining.Name);
        Assert.Equal(remaining.Name, SessionOf(Assert.Single(await ControlClientsAsync(raw, token))));
        Assert.Empty(await server.GetAttachedSessionsAsync(token));
        Assert.False((await remaining.RefreshAsync(token)).Attached);

        // With no session left there is nothing to attach to, and commands say so.
        await remaining.KillAsync(cancellationToken: token);
        Assert.False(await server.IsAliveAsync(token));
    }

    [UnixFact]
    public async Task A_client_that_was_killed_is_started_again_by_the_next_command()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await server.GetSessionsAsync(token);
        string[] first = (await ControlClientsAsync(raw, token))[0].Split('\t');

        using (Process client = Process.GetProcessById(int.Parse(first[1], CultureInfo.InvariantCulture)))
        {
            client.Kill();
            await client.WaitForExitAsync(token);
        }

        Assert.NotEmpty(await server.GetSessionsAsync(token));
        string[] second = Assert.Single(await ControlClientsAsync(raw, token)).Split('\t');
        Assert.NotEqual(first[0], second[0]);
        Assert.Empty(await server.GetClientsAsync(token));
    }

    [UnixFact]
    public async Task A_reply_line_longer_than_a_control_line_is_returned_whole()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await server.GetSessionsAsync(token);
        string client = Assert.Single(await ControlClientsAsync(raw, token));
        const int Length = 70_000;
        RawTmuxResult created = await raw.ExecuteAsync(
            [
                "new-window", "-d", "-P", "-F", "#{pane_id}", "-t", raw.SessionName, "--",
                "/bin/sh", "-c", $"head -c {Length} /dev/zero | tr '\\000' x; sleep 30",
            ],
            token);
        string pane = created.StandardOutputText.Trim();

        TmuxCommandResult captured = await WaitForAsync(
            async () => await server.ExecuteCommandAsync(["capture-pane", "-p", "-J", "-t", pane, "-S", "-"], token),
            result => result.StandardOutputLines.Any(line => line.Length >= Length),
            token);

        Assert.Equal(0, captured.ExitCode);
        Assert.Contains(captured.StandardOutputLines, line => line.Length == Length);

        // The reply came over the client, which a line over its limit would have ended.
        Assert.Equal(client, Assert.Single(await ControlClientsAsync(raw, token)));
    }

    [UnixFact]
    public async Task Detaching_every_other_client_or_a_sessions_clients_leaves_the_library_client_attached()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Session session = Assert.Single(await server.GetSessionsAsync(token));
        string before = Assert.Single(await ControlClientsAsync(raw, token));

        await using PtyAttachedClientScope first = await PtyAttachedClientScope.StartAsync(raw, token);
        _ = await WaitForClientsAsync(server, 1, token);
        await using PtyAttachedClientScope second = await PtyAttachedClientScope.StartAsync(raw, token);
        IReadOnlyList<Client> both = await WaitForClientsAsync(server, 2, token);

        await server.DetachAllClientsAsync(both[0].Name, cancellationToken: token);
        IReadOnlyList<Client> kept = await WaitForClientsAsync(server, 1, token);
        Assert.Equal(both[0].Name, kept[0].Name);
        _ = await server.GetSessionsAsync(token);
        Assert.Equal(before, Assert.Single(await ControlClientsAsync(raw, token)));

        await session.DetachClientAsync(cancellationToken: token);
        _ = await WaitForClientsAsync(server, 0, token);
        _ = await server.GetSessionsAsync(token);
        Assert.Equal(before, Assert.Single(await ControlClientsAsync(raw, token)));
    }

    [UnixFact]
    public async Task A_message_with_no_client_named_is_shown_on_the_other_client()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await server.GetSessionsAsync(token);

        // A control client is told of a message from tmux 3.4.
        if ((await server.InspectAsync(token))!.DaemonVersion < TmuxVersion.Parse("3.4"))
        {
            return;
        }

        await using IControlModeSession other = await server.EnterControlModeAsync(cancellationToken: token);
        string text = $"shown-{Guid.NewGuid():N}";

        await server.DisplayMessageAsync(new DisplayMessageRequest { Message = text }, token);

        // The message is a notification on the client it was shown on. If it
        // went to the library's own client this wait ends by the budget.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TestBudget.Settle);
        await foreach (TmuxEvent seen in other.Events.WithCancellation(budget.Token))
        {
            if (seen is TmuxNotificationEvent { Name: "message" } message
                && string.Join(' ', message.Arguments) == text)
            {
                return;
            }
        }

        Assert.Fail("The message never reached the other client.");
    }

    [UnixFact]
    public async Task A_window_moved_out_of_the_clients_last_window_session_is_reported_as_moved()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await server.CreateSessionAsync(new NewSessionRequest { Name = "spare" }, token);
        IReadOnlyList<Session> sessions = await server.GetSessionsAsync(token);
        string pinned = SessionOf(Assert.Single(await ControlClientsAsync(raw, token)));
        Session from = sessions.Single(session => session.Name == pinned);
        Session into = sessions.Single(session => session.Name != pinned);
        Window only = Assert.Single(await from.GetWindowsAsync(token));

        Window moved = await only.MoveAsync(
            new MoveWindowRequest { Session = into.Name, Destination = "9" },
            token);

        Assert.Equal(only.Id, moved.Id);
        Assert.Single(await server.GetSessionsAsync(token));
        Assert.Equal(into.Name, SessionOf(Assert.Single(await ControlClientsAsync(raw, token))));
    }

    [UnixFact]
    public async Task A_reply_over_the_output_bound_fails_that_command_and_not_the_client()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
                MaxCapturedBytesPerStream = 8192,
            },
            token);
        _ = await server.GetSessionsAsync(token);
        string client = Assert.Single(await ControlClientsAsync(raw, token));
        RawTmuxResult created = await raw.ExecuteAsync(
            [
                "new-window", "-d", "-P", "-F", "#{pane_id}", "-t", raw.SessionName, "--",
                "/bin/sh", "-c", "head -c 20000 /dev/zero | tr '\\000' x; sleep 30",
            ],
            token);
        string pane = created.StandardOutputText.Trim();

        // The row is joined into one line over the bound, whichever transport answers.
        await Assert.ThrowsAnyAsync<LibTmuxException>(() => WaitForAsync(
            async () => await server.ExecuteCommandAsync(["capture-pane", "-p", "-J", "-t", pane, "-S", "-"], token),
            result => result.StandardOutputLines.Any(line => line.Length >= 20000),
            token));

        // The client that carried it went on.
        Assert.NotEmpty(await server.GetSessionsAsync(token));
        Assert.Equal(client, Assert.Single(await ControlClientsAsync(raw, token)));
    }

    [UnixFact]
    public async Task The_client_attached_hook_fires_once_per_attach()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        _ = await raw.ExecuteAsync(
            [
                "set-hook", "-g", "client-attached",
                "set-option -gF @attached \"#{e|+:#{?#{@attached},#{@attached},0},1}\"",
            ],
            token);
        Server server = await ConnectAsync(raw, token);

        for (int command = 0; command < 5; command++)
        {
            _ = await server.GetSessionsAsync(token);
        }

        Assert.Equal("1", await AttachCountAsync(raw, token));

        string[] client = (await ControlClientsAsync(raw, token))[0].Split('\t');
        using (Process process = Process.GetProcessById(int.Parse(client[1], CultureInfo.InvariantCulture)))
        {
            process.Kill();
            await process.WaitForExitAsync(token);
        }

        _ = await server.GetSessionsAsync(token);
        _ = await server.GetSessionsAsync(token);
        Assert.Equal("2", await AttachCountAsync(raw, token));
    }

    private static async Task<string> AttachCountAsync(RawTmuxTestContext raw, CancellationToken token) =>
        (await raw.ExecuteAsync(["show-options", "-gqv", "@attached"], token)).StandardOutputText.Trim();

    // Each row: name, process id, flags, session name, for the control-mode clients only.
    private static async Task<string[]> ControlClientsAsync(RawTmuxTestContext raw, CancellationToken token)
    {
        RawTmuxResult listed = await raw.ExecuteAsync(
            ["list-clients", "-F", "#{client_name}\t#{client_pid}\t#{client_flags}\t#{session_name}\t#{client_control_mode}"],
            token);
        return
        [
            .. listed.StandardOutputLines
                .Where(static line => line.EndsWith("\t1", StringComparison.Ordinal))
                .Select(static line => line[..^2]),
        ];
    }

    private static string SessionOf(string row) => row.Split('\t')[3];

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
            },
            token);

    private static async Task<IReadOnlyList<Client>> WaitForClientsAsync(
        Server server,
        int expected,
        CancellationToken token)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestBudget.Settle;
        IReadOnlyList<Client> clients = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            clients = await server.GetClientsAsync(token);
            if (clients.Count == expected)
            {
                return clients;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        }

        throw new InvalidOperationException($"tmux reports {clients.Count} clients, expected {expected}.");
    }

    private static async Task<T> WaitForAsync<T>(
        Func<Task<T>> read,
        Func<T, bool> done,
        CancellationToken token)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestBudget.Settle;
        T value = await read();
        while (!done(value) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
            value = await read();
        }

        return value;
    }
}
