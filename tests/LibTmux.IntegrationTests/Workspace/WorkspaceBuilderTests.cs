using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Testing;
using LibTmux.Workspace;

// A namespace segment named Workspace would shadow LibTmux.Workspace for
// every file in the assembly, so this sits at the assembly root instead.
namespace LibTmux.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class WorkspaceBuilderTests
{
    private const string Yaml = """
        session_name: libtmux-workspace
        start_directory: /tmp
        options:
          base-index: '1'
        windows:
          - window_name: editor
            layout: even-horizontal
            focus: true
            options:
              automatic-rename: 'off'
            panes:
              - shell_command: echo editor-one
              - shell_command: echo editor-two
                focus: true
          - window_name: shell
            panes:
              - shell_command:
                  - echo command-one
                  - echo command-two
        """;

    [Fact]
    public void Shell_ready_timeout_must_be_positive()
    {
        Server server = Server.Open();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorkspaceBuilder(server, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorkspaceBuilder(server, TimeSpan.FromTicks(-1)));
    }

    [UnixFact]
    public async Task A_workspace_file_becomes_a_session()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryServerScope scope = await factory.CreateServerAsync(
            HarnessOptions(),
            token);

        WorkspaceFile workspace = WorkspaceFile.Parse(Yaml);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspaceResult result = await builder.BuildAsync(workspace, token);

        Assert.Equal("libtmux-workspace", result.Session.Name);
        Assert.Equal(2, result.Windows.Count);
        Assert.Equal(["editor", "shell"], result.Windows.Select(window => window.Name).ToArray());

        // The window options in the file are the ones tmux holds afterwards.
        Assert.Equal(
            "off",
            Assert.Single(await result.Windows[0].Options.GetAsync(
                    new GetOptionRequest("automatic-rename"),
                    token))
                .Value.Raw);

        // Command lists run in order in the same pane.
        IReadOnlyList<Pane> editor = await result.Windows[0].GetPanesAsync(token);
        IReadOnlyList<Pane> shell = await result.Windows[1].GetPanesAsync(token);
        Assert.Equal(2, editor.Count);
        Assert.Single(shell);

        string text = await TmuxWait.UntilAsync(
            async cancellation => string.Join(
                '\n',
                await shell[0].CaptureAsync(cancellationToken: cancellation)),
            captured => captured.Contains("command-two", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(20),
            token);
        Assert.Contains("command-one", text, StringComparison.Ordinal);
        Assert.Contains("command-two", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("command-one", StringComparison.Ordinal)
                < text.IndexOf("command-two", StringComparison.Ordinal));

        // The file asks for nothing tmux alone cannot do, so nothing is
        // reported as unsupported.
        Assert.Empty(result.Unsupported);
    }

    [UnixFact]
    public async Task What_tmux_cannot_do_is_reported_rather_than_dropped()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryServerScope scope = await factory.CreateServerAsync(
            HarnessOptions(),
            token);

        WorkspaceFile workspace = WorkspaceFile.Parse("""
            session_name: libtmux-unsupported
            windows:
              - window_name: only
                layout: "0000,not-a-layout"
                panes:
                  - echo hello
            """);

        WorkspaceResult result = await new WorkspaceBuilder(scope.Server)
            .BuildAsync(workspace, token);

        // The session is still built, and the caller is told what was asked
        // for that could not be honoured.
        Assert.Equal("libtmux-unsupported", result.Session.Name);
        Assert.Contains(
            result.Unsupported,
            message => message.Contains("0000,not-a-layout", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task Each_workspace_command_receives_one_enter()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryServerScope scope = await factory.CreateServerAsync(
            HarnessOptions(),
            token);

        WorkspaceFile workspace = WorkspaceFile.Parse("""
            session_name: libtmux-single-enter
            windows:
              - panes:
                  - shell_command: 'printf "ready\n"; read value; printf "got=<%s>\n" "$value"'
            """);
        WorkspaceResult result = await new WorkspaceBuilder(scope.Server)
            .BuildAsync(workspace, token);
        Pane pane = Assert.Single(await Assert.Single(result.Windows).GetPanesAsync(token));

        bool receivedBlankLine = await TmuxWait.UntilAsync(
            async cancellation => string.Join(
                    '\n',
                    await pane.CaptureAsync(cancellationToken: cancellation))
                .Contains("got=<>", StringComparison.Ordinal),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(20),
            throwOnTimeout: false,
            token);

        Assert.False(receivedBlankLine);
    }

    [UnixFact]
    public async Task A_shell_that_consumes_the_probe_times_out_before_user_commands()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string directory = Directory.CreateTempSubdirectory("libtmux-workspace-timeout").FullName;
        string received = Path.Combine(directory, "received");

        try
        {
            string shell = Path.Combine(directory, "sh");
            await File.WriteAllTextAsync(
                shell,
                $$"""
                    #!/bin/sh
                    set -eu
                    IFS= read -r first
                    printf '%s\n' "$first" > {{ShellQuote(received)}}
                    exec /bin/sh
                    """,
                token);
            File.SetUnixFileMode(
                shell,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            TmuxTestFactory factory = new();
            await using TemporaryServerScope scope = await factory.CreateServerAsync(
                HarnessOptions(shell),
                token);
            WorkspaceFile workspace = WorkspaceFile.Parse("""
                session_name: libtmux-shell-timeout
                windows:
                  - panes:
                      - shell_command: echo WORKSPACE_USER_COMMAND
                """);

            TmuxWaitTimeoutException failure = await Assert.ThrowsAsync<TmuxWaitTimeoutException>(
                () => new WorkspaceBuilder(scope.Server, TimeSpan.FromSeconds(1))
                    .BuildAsync(workspace, token));

            Assert.Equal(TimeSpan.FromSeconds(1), failure.Timeout);
            string firstInput = await File.ReadAllTextAsync(received, token);
            Assert.Contains("wait-for -S libtmux-workspace-ready-", firstInput, StringComparison.Ordinal);
            Assert.DoesNotContain("WORKSPACE_USER_COMMAND", firstInput, StringComparison.Ordinal);
            Server server = await scope.Server.ConnectAsync(token);
            Session session = Assert.Single(await server.GetSessionsAsync(token));
            Window window = Assert.Single(await session.GetWindowsAsync(token));
            Pane pane = Assert.Single(await window.GetPanesAsync(token));
            string captured = string.Join(
                '\n',
                await pane.CaptureAsync(cancellationToken: token));
            Assert.DoesNotContain("WORKSPACE_USER_COMMAND", captured, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [UnixFact]
    public async Task First_pane_directory_controls_window_creation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryServerScope scope = await factory.CreateServerAsync(
            HarnessOptions(),
            token);

        WorkspaceFile workspace = WorkspaceFile.Parse("""
            session_name: libtmux-pane-directory
            start_directory: /tmp
            windows:
              - panes:
                  - start_directory: /usr
                    shell_command: pwd
                  - start_directory: /etc
                    shell_command: pwd
            """);
        WorkspaceResult result = await new WorkspaceBuilder(scope.Server)
            .BuildAsync(workspace, token);
        IReadOnlyList<Pane> panes = await Assert.Single(result.Windows).GetPanesAsync(token);

        IReadOnlyList<string> first = await TmuxWait.UntilAsync(
            cancellation => panes[0].CaptureAsync(cancellationToken: cancellation),
            lines => lines.Contains("/usr", StringComparer.Ordinal),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(20),
            token);
        IReadOnlyList<string> second = await TmuxWait.UntilAsync(
            cancellation => panes[1].CaptureAsync(cancellationToken: cancellation),
            lines => lines.Contains("/etc", StringComparer.Ordinal),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(20),
            token);

        Assert.Contains("/usr", first);
        Assert.Contains("/etc", second);
    }

    [UnixFact]
    public async Task Last_focused_window_and_pane_win()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryServerScope scope = await factory.CreateServerAsync(
            HarnessOptions(),
            token);

        WorkspaceFile workspace = WorkspaceFile.Parse("""
            session_name: libtmux-last-focus
            windows:
              - window_name: first
                focus: true
                panes:
                  -
              - window_name: second
                focus: true
                panes:
                  - focus: true
                  - focus: true
            """);
        WorkspaceResult result = await new WorkspaceBuilder(scope.Server)
            .BuildAsync(workspace, token);
        Session session = await result.Session.RefreshAsync(token);
        Window window = await result.Windows[1].RefreshAsync(token);
        IReadOnlyList<Pane> panes = await window.GetPanesAsync(token);

        Assert.Equal(result.Windows[1].Id, session.ActiveWindow.Id);
        Assert.Equal(panes[1].Id, window.ActivePane.Id);
    }

    [UnixFact]
    public void A_file_that_is_not_a_workspace_is_refused()
    {
        Assert.Throws<WorkspaceFormatException>(() => WorkspaceFile.Parse("- just\n- a\n- list\n"));

        // A workspace naming no session, or no windows, could not be built.
        WorkspaceFile nameless = WorkspaceFile.Parse("windows:\n  - window_name: one\n");
        Assert.Null(nameless.SessionName);
        Assert.Single(nameless.Windows);
    }

    private static TmuxTestOptions HarnessOptions(string? shell = null) =>
        new(new ServerConnectionOptions(
            tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"ltw-{Guid.NewGuid():N}"[..20],
            configurationFile: "/dev/null",
            childEnvironment: shell is null
                ? null
                : new Dictionary<string, string?> { ["SHELL"] = shell }));

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
