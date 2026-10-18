using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;

namespace LibTmux.IntegrationTests.Transport;

// What a one-shot call gets from tmux. Each answer comes from tmux treating the
// caller as a client with no session, and each one changes when the shared
// control client (LIBTMUX_CONTROL_TRANSPORT=1) carries the call instead.
[UnsupportedOSPlatform("windows")]
public sealed class SharedClientSemanticsTests
{
    [UnixFact]
    public async Task A_waiting_if_shell_does_not_hold_up_another_command()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await server.GetSessionsAsync(token);

        Task<TmuxCommandResult> blocked = server.ExecuteCommandAsync(
            ["if-shell", "-F", "1", "wait-for -S probe-entered ; wait-for probe-release"],
            token);
        try
        {
            // The blocking command has reached tmux once it signals.
            _ = await raw.ExecuteAsync(["wait-for", "probe-entered"], token);
            _ = await server.GetSessionsAsync(token);
        }
        finally
        {
            _ = await raw.ExecuteAsync(["wait-for", "-S", "probe-release"], CancellationToken.None);
            _ = await blocked;
        }
    }

    [UnixFact]
    public async Task A_new_window_without_a_directory_starts_where_the_caller_is()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        DirectoryInfo sessionDirectory = Directory.CreateTempSubdirectory("ltcs-session-");
        try
        {
            Session session = await NewSessionAsync(raw, sessionDirectory.FullName, token);
            string seen = await RunInNewWindowAsync(raw, session, null, "pwd -P", token);
            Assert.Equal(PhysicalPath.Resolve(Environment.CurrentDirectory), seen);
        }
        finally
        {
            sessionDirectory.Delete(recursive: true);
        }
    }

    [UnixFact]
    public async Task A_relative_start_directory_is_taken_from_where_the_caller_is()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        DirectoryInfo sessionDirectory = Directory.CreateTempSubdirectory("ltcs-session-");
        string name = $"ltcs-rel-{Guid.NewGuid():N}"[..20];
        string underCaller = Path.Combine(Environment.CurrentDirectory, name);
        _ = Directory.CreateDirectory(underCaller);
        _ = Directory.CreateDirectory(Path.Combine(sessionDirectory.FullName, name));
        try
        {
            Session session = await NewSessionAsync(raw, sessionDirectory.FullName, token);
            string seen = await RunInNewWindowAsync(raw, session, name, "pwd -P", token);
            Assert.Equal(PhysicalPath.Resolve(underCaller), seen);
        }
        finally
        {
            Directory.Delete(underCaller);
            sessionDirectory.Delete(recursive: true);
        }
    }

    [UnixFact]
    public async Task A_new_pane_takes_the_callers_path()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        DirectoryInfo sessionDirectory = Directory.CreateTempSubdirectory("ltcs-session-");
        try
        {
            Session session = await NewSessionAsync(raw, sessionDirectory.FullName, token);
            _ = await raw.ExecuteAsync(
                ["set-environment", "-t", session.Id.ToString(), "PATH", "/probe-session-path"],
                token);
            string seen = await RunInNewWindowAsync(raw, session, null, "printf '%s' \"$PATH\"", token);
            Assert.Equal(Environment.GetEnvironmentVariable("PATH"), seen);
        }
        finally
        {
            sessionDirectory.Delete(recursive: true);
        }
    }

    [UnixFact]
    public async Task A_command_with_no_target_resolves_to_the_most_recent_session()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await server.GetSessionsAsync(token);
        string newer = $"ltcs-newer-{Guid.NewGuid():N}"[..22];
        _ = await raw.ExecuteAsync(["new-session", "-d", "-s", newer], token);

        TmuxCommandResult result = await server.ExecuteCommandAsync(
            ["display-message", "-p", "#{session_name}"],
            token);
        Assert.Equal(newer, string.Join("\n", result.StandardOutputLines).Trim());
    }

    [UnixFact]
    public async Task A_window_the_library_selects_still_follows_its_other_clients()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Session session = Assert.Single(await server.GetSessionsAsync(token));
        Window window = Assert.Single(await session.GetWindowsAsync(token));

        // Two sized clients stand in for human terminals on the session.
        await using IControlModeSession first = await server.EnterControlModeAsync(raw.SessionName, token);
        await using IControlModeSession second = await server.EnterControlModeAsync(raw.SessionName, token);
        _ = await first.SendAsync(TmuxCommand.Create("refresh-client", "-C", "120x40"), token);
        _ = await second.SendAsync(TmuxCommand.Create("refresh-client", "-C", "100x30"), token);
        _ = await first.SendAsync(TmuxCommand.Create("select-window", "-t", window.Id.ToString()), token);
        string before = await SizeAsync(raw, window, token);

        _ = await window.SelectAsync(token);
        _ = await first.SendAsync(TmuxCommand.Create("refresh-client", "-C", "110x35"), token);
        string after = await SizeAsync(raw, window, token);

        Assert.Equal("120x40", before);
        Assert.Equal("110x35", after);
    }

    [UnixFact]
    public async Task A_server_set_to_exit_unattached_with_no_other_client_exits()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await server.GetSessionsAsync(token);

        _ = await raw.ExecuteAsync(["set-option", "-g", "exit-unattached", "on"], token);

        Assert.NotEqual(0, (await raw.ExecuteAsync(["has-session"], token)).ExitCode);
    }

    [UnixFact]
    public async Task A_session_set_to_destroy_unattached_with_no_other_client_ends()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await server.GetSessionsAsync(token);

        _ = await raw.ExecuteAsync(["set-option", "-t", raw.SessionName, "destroy-unattached", "on"], token);

        Assert.NotEqual(0, (await raw.ExecuteAsync(["has-session", "-t", "=" + raw.SessionName], token)).ExitCode);
    }

    private static async Task<string> SizeAsync(RawTmuxTestContext raw, Window window, CancellationToken token) =>
        (await raw.ExecuteAsync(
            ["display-message", "-p", "-t", window.Id.ToString(), "#{window_width}x#{window_height}"],
            token)).StandardOutputText.Trim();

    [UnixFact]
    public async Task A_client_format_with_no_client_named_names_no_client()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        _ = await server.GetSessionsAsync(token);

        TmuxCommandResult result = await server.ExecuteCommandAsync(
            ["display-message", "-p", "#{client_name}|#{client_control_mode}"],
            token);
        Assert.Equal("|", string.Join("\n", result.StandardOutputLines).Trim());
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

    // A session of its own, made by a process, and a library connection that
    // has already sent a command, so the shared client is attached.
    private static async Task<Session> NewSessionAsync(
        RawTmuxTestContext raw,
        string directory,
        CancellationToken token)
    {
        string name = $"ltcs-probe-{Guid.NewGuid():N}"[..22];
        _ = await raw.ExecuteAsync(["new-session", "-d", "-s", name, "-c", directory], token);
        Server server = await ConnectAsync(raw, token);
        return Assert.Single(
            await server.GetSessionsAsync(token),
            session => session.Name == name);
    }

    // Runs a probe command as a new window's program and returns what it wrote.
    private static async Task<string> RunInNewWindowAsync(
        RawTmuxTestContext raw,
        Session session,
        string? startDirectory,
        string probe,
        CancellationToken token)
    {
        string output = Path.Combine(Path.GetTempPath(), $"ltcs-out-{Guid.NewGuid():N}");
        string channel = $"ltcs-done-{Guid.NewGuid():N}";
        // By full path: the pane's PATH is one of the things under test.
        string tmux = Path.IsPathRooted(raw.TmuxBinaryPath)
            ? raw.TmuxBinaryPath
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory, raw.TmuxBinaryPath))
                .First(File.Exists);
        string command =
            $"{probe} > {Quote(output)}; {Quote(tmux)} -S {Quote(raw.SocketPath)} wait-for -S {channel}; exec /bin/cat";
        _ = await session.CreateWindowAsync(
            new NewWindowRequest { StartDirectory = startDirectory, Command = command },
            token);
        _ = await raw.ExecuteAsync(["wait-for", channel], token);
        try
        {
            return File.ReadAllText(output).TrimEnd('\n');
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
