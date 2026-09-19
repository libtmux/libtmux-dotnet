using System.Runtime.Versioning;
// A namespace segment named Workspace would shadow LibTmux.Workspace for
// every file in the assembly, so this sits at the assembly root instead.
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Testing;
using LibTmux.Workspace;

namespace LibTmux.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class WorkspaceBuilderTests
{

    // The library's ten-second default is ample for one shell on an idle
    // machine. These run beside other suites and a build, where a shell can
    // take longer to draw its first prompt than the subject under test needs.
    private static readonly TimeSpan Readiness = TimeSpan.FromSeconds(60);
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
    public void Readiness_timeout_must_be_positive()
    {
        Server server = Server.Open();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorkspaceBuilder(server, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorkspaceBuilder(server, TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorkspaceBuilder(
                server,
                paneReadiness: (PaneReadiness)int.MaxValue));
    }

    [Theory]
    [InlineData(PaneReadiness.Auto, "", "/bin/zsh", "zsh")]
    [InlineData(PaneReadiness.Auto, "", "/bin/bash", "bash")]
    [InlineData(PaneReadiness.Always, "", "/bin/bash", "bash")]
    [InlineData(PaneReadiness.Never, "", "/bin/zsh", null)]
    [InlineData(PaneReadiness.Always, "top", "/bin/zsh", null)]
    public void Readiness_policy_selects_default_shell_panes(
        PaneReadiness policy,
        string defaultCommand,
        string defaultShell,
        string? expected)
    {
        Assert.Equal(
            expected,
            PaneReadinessWaiter.SelectShell(policy, defaultCommand, defaultShell));
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
        Assert.Equal([1, 2], result.Windows.Select(window => window.Index).ToArray());
        Assert.Equal(result.Windows[0].Id, result.Session.ActiveWindow.Value.Id);

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
            TestBudget.Settle,
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

    // A session the builder creates is 80x24, and halving the previous pane
    // in turn runs out of rows before the fifth. Rebalancing between splits
    // is what the CLI learned; the library builds the same window.
    [UnixFact]
    public async Task A_window_of_six_panes_builds_at_the_default_size()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryServerScope scope = await factory.CreateServerAsync(
            HarnessOptions(),
            token);

        WorkspaceFile workspace = WorkspaceFile.Parse("""
            session_name: libtmux-six
            windows:
              - window_name: six
                panes: [one, two, three, four, five, six]
            """);
        WorkspaceBuilder builder = new(scope.Server, Readiness);
        WorkspaceResult result = await builder.BuildAsync(workspace, token);

        IReadOnlyList<Pane> panes = await Assert.Single(result.Windows).GetPanesAsync(token);
        Assert.Equal(6, panes.Count);
        Assert.Empty(result.Unsupported);
    }

    [Theory]
    [InlineData("32d2,80x24,0,0{}")]
    [InlineData("ffff,80x24,0,0,0")]
    [InlineData("b25d,80x24,0,0,0")]
    public async Task Invalid_later_layout_is_refused_before_workspace_creation(string layout)
    {
        Server server = Server.Open(new ServerConnectionOptions { 
            TmuxBinaryPath = "/tmp/libtmux-dotnet-test/missing-layout-backend",
            SocketPath = "/tmp/libtmux-dotnet-test/unused-layout-socket" });
        WorkspaceFile workspace = WorkspaceFile.Parse($$"""
            session_name: invalid-layout
            windows:
              - panes: [null]
              - layout: "{{layout}}"
                panes: [null, null]
            """);

        await Assert.ThrowsAsync<WorkspaceFormatException>(() =>
            new WorkspaceBuilder(server).BuildAsync(workspace, TestContext.Current.CancellationToken));
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
                layout: "79f5,80x24,0,0{39x23,0,0,0,40x24,40,0,1}"
                panes:
                  - echo hello
                  - echo other
            """);

        WorkspaceResult result = await new WorkspaceBuilder(scope.Server, Readiness)
            .BuildAsync(workspace, token);

        // The session is still built, and the caller is told what was asked
        // for that could not be honoured.
        Assert.Equal("libtmux-unsupported", result.Session.Name);
        Assert.Contains(
            result.Unsupported,
            message => message.Contains("79f5,80x24,0,0{39x23,0,0,0,40x24,40,0,1}", StringComparison.Ordinal));
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
        WorkspaceResult result = await new WorkspaceBuilder(scope.Server, Readiness)
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
    public async Task Readiness_timeout_writes_nothing_to_the_pane()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string directory = Directory.CreateTempSubdirectory("libtmux-workspace-timeout").FullName;

        try
        {
            (string shell, string received) = await WriteReceiverAsync(
                directory,
                "sh",
                writeStartup: false,
                token);
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

            WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(
                () => new WorkspaceBuilder(
                        scope.Server,
                        TimeSpan.FromMilliseconds(250),
                        PaneReadiness.Always)
                    .BuildAsync(workspace, token));

            TmuxWaitTimeoutException timeout = Assert.IsType<TmuxWaitTimeoutException>(
                failure.InnerException);
            Assert.Equal(TimeSpan.FromMilliseconds(250), timeout.Timeout);
            Assert.False(File.Exists(received));
            Server server = await scope.Server.ConnectAsync(token);
            Session session = Assert.Single(await server.GetSessionsAsync(token));
            Window window = Assert.Single(await session.GetWindowsAsync(token));
            Pane pane = Assert.Single(await window.GetPanesAsync(token));
            WorkspaceResult partial = Assert.IsType<WorkspaceResult>(failure.PartialResult);
            Assert.Equal(session.Id, partial.Session.Id);
            Assert.Equal(window.Id, Assert.Single(partial.Windows).Id);
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
    public async Task Startup_output_can_look_ready_before_a_prompt_exists()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string directory = Directory.CreateTempSubdirectory("libtmux-workspace-heuristic").FullName;

        try
        {
            // Script-backed shell process names differ by platform. A startup
            // profile keeps pane_current_command bound to a real /bin/bash.
            (string configuration, string profile, string received) =
                await WriteStartupProfileAsync(
                directory,
                token);
            TmuxTestFactory factory = new();
            await using TemporaryServerScope scope = await factory.CreateServerAsync(
                StartupProfileHarnessOptions(configuration, profile, directory),
                token);
            WorkspaceFile workspace = WorkspaceFile.Parse("""
                session_name: libtmux-shell-false-positive
                windows:
                  - panes:
                      - shell_command: WORKSPACE_USER_COMMAND
                """);

            _ = await new WorkspaceBuilder(
                    scope.Server,
                    Readiness,
                    PaneReadiness.Always)
                .BuildAsync(workspace, token);

            string firstInput = await TmuxWait.UntilAsync(
                async cancellation => File.Exists(received)
                    ? await File.ReadAllTextAsync(received, cancellation)
                    : "",
                input => input.Length > 0,
                TestBudget.Settle,
                TimeSpan.FromMilliseconds(20),
                token);
            Assert.Equal("WORKSPACE_USER_COMMAND\n", firstInput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [UnixFact]
    public async Task Session_options_launch_the_real_first_pane()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string directory = Directory.CreateTempSubdirectory("libtmux-workspace-first-pane").FullName;

        try
        {
            (string command, string received) = await WriteReceiverAsync(
                directory,
                "receiver",
                writeStartup: false,
                token);
            TmuxTestFactory factory = new();
            await using TemporaryServerScope scope = await factory.CreateServerAsync(
                HarnessOptions(),
                token);
            WorkspaceFile workspace = new(
                sessionName: "libtmux-first-pane-options",
                options: new Dictionary<string, string>
                {
                    ["base-index"] = "3",
                    ["default-command"] = ShellQuote(command),
                },
                windows:
                [
                    new WorkspaceWindow(
                        windowName: "configured",
                        panes: [new WorkspacePane(["WORKSPACE_USER_COMMAND"])])
                ]);

            WorkspaceResult result = await new WorkspaceBuilder(
                    scope.Server,
                    Readiness,
                    PaneReadiness.Always)
                .BuildAsync(workspace, token);

            string firstInput = await TmuxWait.UntilAsync(
                async cancellation => File.Exists(received)
                    ? await File.ReadAllTextAsync(received, cancellation)
                    : "",
                input => input.Length > 0,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(20),
                token);
            Window window = Assert.Single(result.Windows);
            Assert.Equal(3, window.Index);
            Assert.Equal("configured", window.Name);
            Assert.Equal("WORKSPACE_USER_COMMAND\n", firstInput);
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
        WorkspaceResult result = await new WorkspaceBuilder(scope.Server, Readiness)
            .BuildAsync(workspace, token);
        IReadOnlyList<Pane> panes = await Assert.Single(result.Windows).GetPanesAsync(token);

        IReadOnlyList<string> first = await TmuxWait.UntilAsync(
            cancellation => panes[0].CaptureAsync(cancellationToken: cancellation),
            lines => lines.Contains("/usr", StringComparer.Ordinal),
            TestBudget.Settle,
            TimeSpan.FromMilliseconds(20),
            token);
        IReadOnlyList<string> second = await TmuxWait.UntilAsync(
            cancellation => panes[1].CaptureAsync(cancellationToken: cancellation),
            lines => lines.Contains("/etc", StringComparer.Ordinal),
            TestBudget.Settle,
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
        WorkspaceResult result = await new WorkspaceBuilder(scope.Server, Readiness)
            .BuildAsync(workspace, token);
        Session session = await result.Session.RefreshAsync(token);
        Window window = await result.Windows[1].RefreshAsync(token);
        IReadOnlyList<Pane> panes = await window.GetPanesAsync(token);

        Assert.Equal(result.Windows[1].Id, session.ActiveWindow.Value.Id);
        Assert.Equal(panes[1].Id, window.ActivePane.Value.Id);
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
        new(new ServerConnectionOptions
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = $"ltw-{Guid.NewGuid():N}"[..20],
            ConfigurationFile = shell is null
                ? "/dev/null"
                : Path.ChangeExtension(shell, ".tmux.conf"),
            ChildEnvironment = shell is null
                ? null
                : new Dictionary<string, string?> { ["SHELL"] = shell }
        });

    private static TmuxTestOptions StartupProfileHarnessOptions(
        string configuration,
        string profile,
        string home) =>
        new(new ServerConnectionOptions
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = $"ltw-{Guid.NewGuid():N}"[..20],
            ConfigurationFile = configuration,
            ChildEnvironment = new Dictionary<string, string?>
            {
                ["BASH_ENV"] = profile,
                ["HOME"] = home,
                ["SHELL"] = "/bin/bash",
            }
        });

    private static async Task<(string Configuration, string Profile, string Received)>
        WriteStartupProfileAsync(
            string directory,
            CancellationToken cancellationToken)
    {
        string configuration = Path.Combine(directory, "tmux.conf");
        string profile = Path.Combine(directory, ".bash_profile");
        string received = Path.Combine(directory, "received");
        await File.WriteAllTextAsync(
            profile,
            "unset BASH_ENV\nprintf 'startup output\\n'\nIFS= read -r first\n"
            + $"printf '%s\\n' \"$first\" > {ShellQuote(received)}\n",
            cancellationToken);
        await File.WriteAllTextAsync(
            configuration,
            "set-option -g default-shell /bin/bash\n",
            cancellationToken);
        return (configuration, profile, received);
    }

    private static async Task<(string Program, string Received)> WriteReceiverAsync(
        string directory,
        string name,
        bool writeStartup,
        CancellationToken cancellationToken)
    {
        string program = Path.Combine(directory, name);
        string received = Path.Combine(directory, "received");
        string startup = writeStartup ? "printf 'startup output\\n'\n" : "";
        await File.WriteAllTextAsync(
            program,
            $"#!/bin/sh\nset -eu\n{startup}IFS= read -r first\n"
            + $"printf '%s\\n' \"$first\" > {ShellQuote(received)}\nexec /bin/sh\n",
            cancellationToken);
        File.SetUnixFileMode(
            program,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(
            Path.ChangeExtension(program, ".tmux.conf"),
            $"set-option -g default-shell {ShellQuote(program)}\n",
            cancellationToken);
        return (program, received);
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
