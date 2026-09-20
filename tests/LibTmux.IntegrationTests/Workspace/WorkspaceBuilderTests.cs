using System.Runtime.Versioning;
// A namespace segment named Workspace would shadow LibTmux.Workspace for
// every file in the assembly, so this sits at the assembly root instead.
using LibTmux.IntegrationTests.Transport;
using LibTmux.Testing;
using LibTmux.Workspace;

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

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(6, "tiled")]
    [InlineData(12, "tiled")]
    [InlineData(6, "even-horizontal")]
    public async Task Construction_arranges_panes_without_changing_final_layout_order_or_focus(int count, string layout)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(HarnessOptions(), token);
        WorkspaceFile workspace = new("construction-layout", options: new Dictionary<string, string>
        {
            ["default-command"] = "exec /bin/cat",
            ["default-size"] = "80x24",
        }, windows:
        [
            new WorkspaceWindow("many", layout: layout, focus: true,
                panes: [.. Enumerable.Range(0, count).Select(index => new WorkspacePane(focus: index == 2))]),
            new WorkspaceWindow("other"),
        ]);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(workspace, cancellationToken: token);
        WorkspaceResult result = await builder.ApplyAsync(plan, token);

        Window window = result.Windows[0];
        Assert.Equal(80, window.Width);
        Assert.InRange(window.Height, 23, 24);
        Assert.Equal(count, window.Panes.Count);
        Assert.Equal(Enumerable.Range(0, count), window.Panes.Select(pane => pane.Index));
        Assert.Equal(result.Journal.Where(outcome => outcome.Action.Target.StartsWith("window:0/", StringComparison.Ordinal)
            && outcome.Action.Kind is WorkspaceActionKind.CaptureFirstPane or WorkspaceActionKind.SplitPane)
            .Select(outcome => Assert.IsType<Pane>(outcome.Result).Id), window.Panes.Select(pane => pane.Id));
        Assert.Equal(window.Panes[2].Id, window.ActivePane.Value.Id);
        Assert.Equal(window.Id, result.Session.ActiveWindow.Value.Id);
        WorkspaceActionOutcome finalLayout = Assert.Single(result.Journal, outcome => outcome.Action.Kind == WorkspaceActionKind.SelectLayout);
        Assert.Equal(layout, Assert.IsType<WorkspaceAction<SelectLayoutRequest>>(finalLayout.Action).Request.Layout);
        Assert.Equal(window.Layout, Assert.IsType<Window>(finalLayout.Result).Layout);
        Assert.All(window.Panes, pane => Assert.True(pane.Width < window.Width));
        if (layout == "even-horizontal")
            Assert.All(window.Panes, pane => Assert.Equal(window.Height, pane.Height));
        else
            Assert.All(window.Panes, pane => Assert.True(pane.Height < window.Height));
        Assert.Equal(count - 2, plan.Actions.Count(action => action.Kind == WorkspaceActionKind.ArrangePanes));
        foreach (WorkspaceAction action in plan.Actions.Where(action => action.Kind == WorkspaceActionKind.ArrangePanes))
            Assert.Equal("tiled", Assert.IsType<WorkspaceAction<SelectLayoutRequest>>(action).Request.Layout);
        Assert.All(result.Journal, outcome => Assert.Equal(WorkspaceActionState.Completed, outcome.State));
    }

    [UnixFact]
    public async Task Rejected_construction_layout_stops_before_the_next_split()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxCommandException? rejected = null;
        TmuxInterceptor interceptor = async (invocation, next, cancellation) =>
        {
            if (invocation.Arguments.Contains("select-layout", StringComparer.Ordinal)
                && invocation.Arguments.Contains("tiled", StringComparer.Ordinal))
            {
                rejected = new("Construction layout was rejected.", new TmuxCommandResult(invocation.Arguments, 1,
                    ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, [], ["layout rejected"]));
                throw rejected;
            }
            return await next(cancellation);
        };
        TmuxTestOptions options = new(HarnessOptions().ConnectionOptions with { Interceptor = interceptor });
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(options, token);
        WorkspaceFile workspace = new("rejected-construction", options: new Dictionary<string, string>
        {
            ["default-command"] = "exec /bin/cat",
        }, windows: [new WorkspaceWindow(layout: "even-horizontal", panes: [new(), new(), new()])]);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(workspace, cancellationToken: token);
        WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, token));

        Assert.NotNull(rejected);
        Assert.Same(rejected, failure.InnerException);
        Assert.Equal(WorkspaceActionState.Failed, Assert.Single(failure.Journal,
            outcome => outcome.Action.Kind == WorkspaceActionKind.ArrangePanes).State);
        Assert.Equal(WorkspaceActionState.NotStarted, Assert.Single(failure.Journal,
            outcome => outcome.Action.Kind == WorkspaceActionKind.SplitPane && outcome.Action.Target == "window:0/pane:2").State);
        Assert.NotNull(failure.PartialResult);
        Assert.Empty(failure.PartialResult.Unsupported);
    }

    [UnixFact]
    public async Task Resolved_directories_reach_the_panes_from_the_document_origin()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string origin = Directory.CreateTempSubdirectory("libtmux-workspace-origin-").FullName;
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(origin, "#{session_name}-#[bold]")).FullName;
            string tests = Directory.CreateDirectory(Path.Combine(source, "#{pane_id}-##[literal]")).FullName;
            TmuxTestFactory factory = new();
            await using TemporaryServerScope scope = await factory.CreateServerAsync(HarnessOptions(), token);
            WorkspaceFile workspace = WorkspaceFile.Parse("""
                session_name: resolved-directories
                start_directory: '#{session_name}-#[bold]'
                options:
                  default-command: exec /bin/cat
                windows:
                  - window_name: project
                    panes:
                      - shell_command: []
                      - shell_command: []
                        start_directory: '#{pane_id}-##[literal]'
                """).Resolve(origin).WithDefaults();

            WorkspaceResult result = await new WorkspaceBuilder(scope.Server)
                .BuildAsync(workspace, token);
            IReadOnlyList<Pane> panes = await Assert.Single(result.Windows).GetPanesAsync(token);

            Assert.Equal(2, panes.Count);
            Assert.Equal(source, panes[0].CurrentPath);
            Assert.Equal(tests, panes[1].CurrentPath);

            WorkspaceFile native = new(
                sessionName: "native-directories",
                startDirectory: origin,
                options: workspace.Options,
                windows: [new WorkspaceWindow(), new WorkspaceWindow(startDirectory: "#{session_path}")]);
            WorkspaceResult nativeResult = await new WorkspaceBuilder(scope.Server)
                .BuildAsync(native, token);
            Pane nativePane = Assert.Single(await nativeResult.Windows[1].GetPanesAsync(token));
            Assert.Equal(origin, nativePane.CurrentPath);
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    [UnixFact]
    public async Task Environment_and_before_commands_inherit_in_declaration_order()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string origin = Directory.CreateTempSubdirectory("libtmux-workspace-inheritance-").FullName;
        try
        {
            TmuxTestFactory factory = new();
            await using TemporaryServerScope scope = await factory.CreateServerAsync(HarnessOptions(), token);
            string output = Path.Combine(origin, "result");
            string destination = ShellQuote(output);
            string tmux = ShellQuote(Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux");
            string ready = $"workspace-ready-{Guid.NewGuid():N}";
            await scope.Server.CreateSessionAsync(new NewSessionRequest
            {
                Name = "wait-anchor",
                Command = "exec /bin/cat",
            }, token);
            await using TmuxWaitChannel wait = scope.Server.OpenWaitChannel(ready);
            WorkspaceFile workspace = WorkspaceFile.Parse($$"""
                session_name: inherited-declarations
                environment:
                  SESSION_VALUE: present
                  LAYER: session
                options:
                  default-command: exec /bin/sh
                shell_command_before: >-
                  printf 'session:' >> {{destination}}
                windows:
                  - window_name: project
                    environment:
                      LAYER: window
                    shell_command_before: >-
                      printf 'window:' >> {{destination}}
                    panes:
                      - environment:
                          LAYER: pane
                        shell_command_before: >-
                          printf 'pane:' >> {{destination}}
                        shell_command: >-
                          printf '%s:%s:done' "$SESSION_VALUE" "$LAYER" >> {{destination}}; {{tmux}} wait-for -S {{ready}}
                """).Resolve(origin);

            WorkspaceResult result = await new WorkspaceBuilder(scope.Server)
                .BuildAsync(workspace, token);

            Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(1), token));
            Assert.Equal("session:window:pane:present:pane:done", await File.ReadAllTextAsync(output, token));
            Assert.Equal("inherited-declarations", result.Session.Name);
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    [UnixFact]
    public async Task A_workspace_file_becomes_a_session()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxTestFactory factory = new();
        await using TemporaryServerScope scope = await factory.CreateServerAsync(
            HarnessOptions(),
            token);

        string completed = $"workspace-built-{Guid.NewGuid():N}";
        string tmux = ShellQuote(Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux");
        WorkspaceFile workspace = WorkspaceFile.Parse(Yaml.Replace("echo command-two",
            $"echo command-two; {tmux} wait-for -S {completed}", StringComparison.Ordinal));
        WorkspaceBuilder builder = new(scope.Server);
        WorkspaceResult result = await builder.BuildAsync(workspace, token);

        Assert.NotEmpty(result.Journal);
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

        await using TmuxWaitChannel completion = result.Session.Server.OpenWaitChannel(completed);
        Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(1), token));
        string text = string.Join('\n', await shell[0].CaptureAsync(cancellationToken: token));
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
        string directory = Directory.CreateTempSubdirectory("libtmux-workspace-enter-").FullName;
        try
        {
            await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(HarnessOptions(), token);
            string received = Path.Combine(directory, "received");
            string completed = $"workspace-enter-{Guid.NewGuid():N}";
            string tmux = ShellQuote(Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux");
            string command = $"IFS= read -r value; printf '%s' \"$value\" > {ShellQuote(received)}; {tmux} wait-for -S {completed}";
            WorkspaceFile workspace = new("libtmux-single-enter", windows:
                [new WorkspaceWindow(panes: [new WorkspacePane([command])])]);

            WorkspaceResult result = await new WorkspaceBuilder(scope.Server).BuildAsync(workspace, token);
            Pane pane = Assert.Single(await Assert.Single(result.Windows).GetPanesAsync(token));
            await using TmuxWaitChannel completion = result.Session.Server.OpenWaitChannel(completed);
            await pane.SendTextAsync("expected-input", cancellationToken: token);

            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(1), token));
            Assert.Equal("expected-input", await File.ReadAllTextAsync(received, token));
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
        string directory = Directory.CreateTempSubdirectory("libtmux-workspace-first-pane-").FullName;
        try
        {
            string command = Path.Combine(directory, "receiver");
            string received = Path.Combine(directory, "received");
            string completed = $"workspace-options-{Guid.NewGuid():N}";
            string tmux = ShellQuote(Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux");
            await File.WriteAllTextAsync(command,
                "#!/bin/sh\nset -eu\nIFS= read -r first\n"
                + $"printf '%s\\n' \"$first\" > {ShellQuote(received)}\n"
                + $"{tmux} wait-for -S {completed}\nexec /bin/sh\n", token);
            File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(HarnessOptions(), token);
            WorkspaceFile workspace = new("libtmux-first-pane-options",
                options: new Dictionary<string, string>
                {
                    ["base-index"] = "3",
                    ["default-command"] = ShellQuote(command),
                },
                windows: [new WorkspaceWindow("configured", panes: [new WorkspacePane(["WORKSPACE_USER_COMMAND"])])]);

            WorkspaceResult result = await new WorkspaceBuilder(scope.Server).BuildAsync(workspace, token);

            await using TmuxWaitChannel completion = result.Session.Server.OpenWaitChannel(completed);
            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(1), token));
            Window window = Assert.Single(result.Windows);
            Assert.Equal(3, window.Index);
            Assert.Equal("configured", window.Name);
            Assert.Equal("WORKSPACE_USER_COMMAND\n", await File.ReadAllTextAsync(received, token));
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

        Assert.Equal(2, panes.Count);
        Assert.Equal("/usr", panes[0].CurrentPath);
        Assert.Equal("/etc", panes[1].CurrentPath);
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
        Session session = result.Session;
        Window window = result.Windows[1];
        CapturedRelation<Pane> panes = window.Panes;

        Assert.Contains(result.Journal, action => action.Action.Kind == WorkspaceActionKind.CaptureResult
            && action.State == WorkspaceActionState.Completed);
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

    private static TmuxTestOptions HarnessOptions() =>
        new(new ServerConnectionOptions
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = $"ltw-{Guid.NewGuid():N}"[..20],
            ConfigurationFile = "/dev/null",
            ChildEnvironment = new Dictionary<string, string?>
            {
                ["SHELL"] = "/bin/sh",
                ["ENV"] = null,
                ["BASH_ENV"] = null,
            },
        });

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
