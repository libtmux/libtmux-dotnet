using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Testing;
using LibTmux.Workspace;

namespace LibTmux.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class WorkspacePlanTests
{
    [UnixFact]
    public async Task Window_options_precede_startup_commands_and_layout()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        WorkspaceFile workspace = new("staged", windows:
        [new WorkspaceWindow(layout: "main-horizontal", options: new Dictionary<string, string>
        {
            ["main-pane-height"] = "5",
        }, panes: [new WorkspacePane(["left"]), new WorkspacePane(["right"])])]);
        WorkspacePlan plan = await new WorkspaceBuilder(scope.Server).PlanAsync(workspace, cancellationToken: token);
        WorkspaceAction[] actions = [.. plan.Actions];
        int option = Array.FindIndex(actions, action => action is WorkspaceAction<SetOptionRequest> setting
            && setting.Request.Name == "main-pane-height");
        int capture = Array.FindIndex(actions, action => action.Kind == WorkspaceActionKind.CaptureFirstPane);
        int send = Array.FindIndex(actions, action => action.Kind == WorkspaceActionKind.SendText);
        int split = Array.FindIndex(actions, action => action.Kind == WorkspaceActionKind.SplitPane);
        int layout = Array.FindIndex(actions, action => action.Kind == WorkspaceActionKind.SelectLayout);

        Assert.True(capture < option && option < send && option < split && option < layout,
            $"Window option at {option} must follow capture {capture} and precede input {send}, split {split}, layout {layout}.");
    }

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(WorkspaceExistingSession.Error)]
    [InlineData(WorkspaceExistingSession.Append)]
    [InlineData(WorkspaceExistingSession.Replace)]
    [InlineData(WorkspaceExistingSession.Reuse)]
    public async Task Global_and_post_construction_options_are_explicit_reviewed_effects(WorkspaceExistingSession policy)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        Session borrowed = await scope.Server.CreateSessionAsync(new()
        {
            Name = policy == WorkspaceExistingSession.Error ? "borrowed" : "staged",
            Command = "exec /bin/cat",
        }, token);
        await borrowed.Options.SetAsync(new("@workspace-global", "original") { Scope = OptionScope.Session, Global = true }, token);
        WorkspaceFile workspace = WorkspaceFile.Parse("""
            session_name: staged
            global_options:
              '@workspace-global': changed
            windows:
              - layout: main-horizontal
                options:
                  main-pane-height: 5
                options_after:
                  synchronize-panes: on
                panes: [left, right]
            """).WithDefaults().Resolve(Path.GetTempPath());
        WorkspacePlan plan = await new WorkspaceBuilder(scope.Server).PlanAsync(workspace, new()
        {
            ExistingSession = policy,
            CompensateOnFailure = true,
        }, token);

        Assert.Equal("original", Assert.Single(await borrowed.Options.GetAsync(
            new("@workspace-global") { Scope = OptionScope.Session, Global = true }, token)).Value.Raw);
        Assert.DoesNotContain(plan.CompensationActions, action => action.Kind == WorkspaceActionKind.SetOption);
        if (policy == WorkspaceExistingSession.Reuse)
        {
            Assert.Equal(WorkspaceActionKind.ReuseSession, Assert.Single(plan.Actions).Kind);
            return;
        }
        WorkspaceAction[] actions = [.. plan.Actions];
        int global = Array.FindIndex(actions, action => action is WorkspaceAction<SetOptionRequest> setting
            && setting.Request.Name == "@workspace-global");
        WorkspaceAction<SetOptionRequest> globalAction = Assert.IsType<WorkspaceAction<SetOptionRequest>>(actions[global]);
        Assert.True(globalAction.Request.Global);
        Assert.Equal(OptionScope.Session, globalAction.Request.Scope);
        Assert.Equal("session", globalAction.Target);
        Assert.True(global < Array.FindIndex(actions, action => action.Kind == WorkspaceActionKind.CreateWindow));
        int after = Array.FindIndex(actions, action => action is WorkspaceAction<SetOptionRequest> setting
            && setting.Request.Name == "synchronize-panes");
        Assert.True(after > Array.FindLastIndex(actions, action => action.Kind == WorkspaceActionKind.SendText));
        Assert.True(after > Array.FindIndex(actions, action => action.Kind == WorkspaceActionKind.SelectLayout));
    }

    [Theory]
    [InlineData("bad:name")]
    [InlineData("bad.name")]
    public async Task Invalid_session_names_are_rejected_before_endpoint_inspection(string name)
    {
        int invocations = 0;
        Server server = Server.Open(new ServerConnectionOptions
        {
            SocketName = $"ltwp-{Guid.NewGuid():N}"[..20],
            TmuxBinaryPath = "/missing-workspace-plan-tmux",
            Interceptor = (_, _, _) =>
            {
                invocations++;
                throw new InvalidOperationException("Invalid workspace reached endpoint inspection.");
            },
        });
        WorkspaceFile workspace = new(name, windows: [new WorkspaceWindow()]);

        Assert.Throws<ArgumentException>(() => WorkspaceBuilder.Validate(workspace));
        await Assert.ThrowsAsync<ArgumentException>(() => new WorkspaceBuilder(server).PlanAsync(
            workspace, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, invocations);
    }

    [Fact]
    public void Local_validation_checks_structure_and_never_executes_host_scripts()
    {
        Assert.Throws<ArgumentNullException>(() => WorkspaceBuilder.Validate(null!));
        Assert.Throws<WorkspaceFormatException>(() => WorkspaceBuilder.Validate(new("planned")));
        WorkspaceFile workspace = new WorkspaceFile("planned", windows: [new WorkspaceWindow()],
            beforeScript: "exit 17").Resolve(Path.GetTempPath());
        Assert.Throws<WorkspaceFormatException>(() => WorkspaceBuilder.Validate(workspace));
        WorkspaceBuilder.Validate(workspace, new() { AllowHostScripts = true });
        WorkspaceBuilder.Validate(workspace, new() { ExistingSession = WorkspaceExistingSession.Reuse });

        WorkspaceFile invalid = new("planned", windows:
            [new WorkspaceWindow(panes: [new WorkspacePane(["bad\0command"])])]);
        Assert.Throws<WorkspaceFormatException>(() => WorkspaceBuilder.Validate(invalid));
    }

    [Fact]
    public void Empty_imported_session_name_reports_its_value_location_after_resolution()
    {
        WorkspaceFile workspace = WorkspaceFile.Parse("session_name: ''\nwindows:\n  - window_name: editor\n")
            .WithDefaults(environment: new Dictionary<string, string> { ["PROJECT"] = "reviewed" })
            .Resolve(Path.GetTempPath());

        WorkspaceFormatException failure = Assert.Throws<WorkspaceFormatException>(
            () => WorkspaceBuilder.Validate(workspace));

        Assert.Contains("session_name", failure.Message, StringComparison.Ordinal);
        Assert.Contains("line 1, column 15", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("session_name: bad-options\nglobal_options:\n  '@global': '${VALUE}'\nwindows: [{}]\n",
        "global_options.@global", "line 3, column 14")]
    [InlineData("{\"session_name\":\"bad-options\",\"global_options\":{\"@global\":\"${VALUE}\"},\"windows\":[{}]}",
        "global_options.@global", "line 1, column 59")]
    [InlineData("session_name: bad-options\nwindows:\n  - options_after:\n      '@after': '${VALUE}'\n",
        "windows[0].options_after.@after", "line 4, column 17")]
    [InlineData("{\"session_name\":\"bad-options\",\"windows\":[{\"options_after\":{\"@after\":\"${VALUE}\"}}]}",
        "windows[0].options_after.@after", "line 1, column 69")]
    public void Resolved_global_and_post_construction_options_retain_validation_locations(
        string document, string path, string location)
    {
        WorkspaceFile declaration = WorkspaceFile.Parse(document).WithDefaults().Resolve(Path.GetTempPath(),
            new Dictionary<string, string> { ["VALUE"] = "bad\0value" });

        WorkspaceFormatException failure = Assert.Throws<WorkspaceFormatException>(
            () => WorkspaceBuilder.Validate(declaration));

        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
        Assert.Contains("NUL", failure.Message, StringComparison.Ordinal);
        Assert.Contains(location, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_declared_window_indexes_fail_before_endpoint_inspection()
    {
        Server absent = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = "/missing-workspace-tmux" });
        WorkspaceFile workspace = WorkspaceFile.Parse("""
            session_name: duplicate-indexes
            windows:
              - window_index: 5
              - window_index: 5
            """).WithDefaults(environment: new Dictionary<string, string> { ["PROJECT"] = "reviewed" })
            .Resolve(Path.GetTempPath());

        WorkspaceFormatException failure = Assert.Throws<WorkspaceFormatException>(
            () => WorkspaceBuilder.Validate(workspace));
        Assert.Contains("window_index 5", failure.Message, StringComparison.Ordinal);
        Assert.Contains("windows[1].window_index", failure.Message, StringComparison.Ordinal);
        Assert.Contains("line 4, column 19", failure.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<WorkspaceFormatException>(() => new WorkspaceBuilder(absent).PlanAsync(
            workspace, cancellationToken: TestContext.Current.CancellationToken));
    }

    [UnixFact]
    public async Task Planning_an_absent_endpoint_freezes_actions_without_starting_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        Dictionary<string, string> environment = new() { ["PROJECT"] = "reviewed" };
        WorkspaceFile workspace = new WorkspaceFile("planned", windows:
        [new WorkspaceWindow("editor", panes: [new WorkspacePane(["echo reviewed"])])])
            .WithDefaults(environment: environment);
        environment["PROJECT"] = "changed";

        WorkspacePlan plan = await new WorkspaceBuilder(scope.Server).PlanAsync(workspace, cancellationToken: token);

        Assert.Null(plan.ObservedServer);
        Assert.Null(plan.ExistingSession);
        Assert.Equal(WorkspaceServerStartup.CreateOrJoin, plan.ServerStartup);
        await Assert.ThrowsAsync<LibTmuxException>(() => new WorkspaceBuilder(scope.Server).PlanAsync(workspace,
            new() { ServerStartup = WorkspaceServerStartup.RequireExisting }, token));
        Assert.Null(await scope.Server.InspectAsync(token));
        WorkspaceAction<NewSessionRequest> create = Assert.IsType<WorkspaceAction<NewSessionRequest>>(plan.Actions[0]);
        Assert.Equal("planned", create.Request.Name);
        Assert.Equal("reviewed", create.Request.Environment!["PROJECT"]);
        Assert.Contains(plan.Actions, action => action.Kind == WorkspaceActionKind.UnlinkWindow && action.Target == "bootstrap");
        SendKeysRequest send = Assert.Single(plan.Actions.OfType<WorkspaceAction<SendKeysRequest>>(),
            action => action.Kind == WorkspaceActionKind.SendText).Request;
        Assert.Equal("echo reviewed", send.Text);
        Assert.True(send.Enter);
        Assert.True(send.Literal);
        Assert.Throws<NotSupportedException>(() => ((IList<WorkspaceAction>)plan.Actions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)create.Request.Environment).Clear());
        Assert.NotEmpty(string.Join('\n', plan.Actions));
        Assert.Null(await scope.Server.InspectAsync(token));
    }

    [UnixFact]
    public async Task Enter_overrides_freeze_effective_requests_across_all_command_scopes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        WorkspaceFile workspace = WorkspaceFile.Parse("""
            session_name: planned
            shell_command_before:
              - cmd: root
                enter: false
              - root-follow
            windows:
              - shell_command_before:
                  - window-follow
                panes:
                  - enter: true
                    shell_command_before:
                      - cmd: pane-reset
                        enter: true
                      - pane-follow
                    shell_command:
                      - cmd: main-hold
                        enter: false
                      - main-follow
            """);

        WorkspacePlan plan = await new WorkspaceBuilder(scope.Server).PlanAsync(workspace, cancellationToken: token);
        SendKeysRequest[] sends = [.. plan.Actions.OfType<WorkspaceAction<SendKeysRequest>>()
            .Select(action => action.Request)];
        Assert.Equal(
            [("root", false), ("root-follow", false), ("window-follow", false),
                ("pane-reset", true), ("pane-follow", true), ("main-hold", false),
                ("main-follow", false)],
            sends.Select(request => (request.Text, request.Enter)));
        Assert.All(sends, request => Assert.True(request.Literal));

        List<WorkspaceCommand> callerCommands = [new("held")];
        WorkspaceFile defaults = new("other", windows: [new WorkspaceWindow(panes:
            [new WorkspacePane(commands: callerCommands, enter: false), new WorkspacePane(["run"])])]);
        WorkspacePlan defaultPlan = await new WorkspaceBuilder(scope.Server).PlanAsync(defaults, cancellationToken: token);
        callerCommands[0] = new("changed", true);
        Assert.Equal(
            [("held", false), ("run", true)],
            defaultPlan.Actions.OfType<WorkspaceAction<SendKeysRequest>>()
                .Select(action => (action.Request.Text, action.Request.Enter)));
        Assert.Null(await scope.Server.InspectAsync(token));
    }

    [UnixFact]
    public async Task Existing_session_policies_name_exact_identities_and_complete_effects()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        Session existing = await scope.Server.CreateSessionAsync(new NewSessionRequest { Name = "planned", Command = "exec /bin/cat" }, token);
        WorkspaceFile workspace = new("planned", options: new Dictionary<string, string> { ["@change"] = "value" },
            windows: [new WorkspaceWindow("added")]);
        WorkspaceBuilder builder = new(scope.Server);

        await Assert.ThrowsAsync<TmuxSessionExistsException>(() => builder.PlanAsync(workspace, cancellationToken: token));
        WorkspacePlan reused = await builder.PlanAsync(workspace, new() { ExistingSession = WorkspaceExistingSession.Reuse }, token);
        Assert.Equal(existing.Id, reused.ExistingSession!.Id);
        Assert.Equal(existing.Generation, reused.ObservedServer!.Generation);
        Assert.Equal(WorkspaceActionKind.ReuseSession, Assert.Single(reused.Actions).Kind);
        Assert.Empty(reused.CompensationActions);

        WorkspacePlan appended = await builder.PlanAsync(workspace, new() { ExistingSession = WorkspaceExistingSession.Append, CompensateOnFailure = true }, token);
        Assert.DoesNotContain(appended.Actions, action => action.Kind is WorkspaceActionKind.CreateSession or WorkspaceActionKind.RemoveSession or WorkspaceActionKind.MoveToBaseIndex);
        Assert.DoesNotContain(appended.Actions, action => action.Kind == WorkspaceActionKind.SetOption && action.Target == "session");
        Assert.Equal(WorkspaceActionKind.UnlinkWindow, Assert.Single(appended.CompensationActions).Kind);

        WorkspacePlan replaced = await builder.PlanAsync(workspace, new() { ExistingSession = WorkspaceExistingSession.Replace }, token);
        Assert.Equal("keepalive", replaced.Actions[0].Target);
        Assert.Equal(WorkspaceActionKind.CreateSession, replaced.Actions[0].Kind);
        Assert.Equal(WorkspaceActionKind.CaptureBootstrap, replaced.Actions[1].Kind);
        Assert.Equal(WorkspaceActionKind.RemoveSession, replaced.Actions[2].Kind);
        Assert.Equal("existing", replaced.Actions[2].Target);
        Assert.Equal(WorkspaceActionKind.UnlinkWindow, replaced.Actions[^2].Kind);
        Assert.Equal(WorkspaceActionKind.CaptureResult, replaced.Actions[^1].Kind);
        Assert.Equal("keepalive-bootstrap", replaced.Actions[^2].Target);
        Assert.Equal("session", replaced.Actions[^1].Target);
        Assert.Equal(existing.Id, Assert.Single(await scope.Server.GetSessionsAsync(token)).Id);
    }

    [UnixFact]
    public async Task Cooperative_readiness_surrounds_startup_and_rejects_reserved_environment()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        WorkspaceFile workspace = new("planned", windows: [new WorkspaceWindow(panes: [new WorkspacePane(["echo ready"]), new WorkspacePane()])]);
        WorkspacePlanOptions options = new() { Readiness = WorkspaceReadiness.Cooperative, ReadinessTimeout = TimeSpan.FromMilliseconds(250) };
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(workspace, options, token);
        List<WorkspaceAction> actions = [.. plan.Actions];
        int create = actions.FindIndex(action => action.Kind == WorkspaceActionKind.CreateWindow);
        Assert.Equal(WorkspaceActionKind.OpenReadinessChannel, actions[create - 1].Kind);
        Assert.Equal(WorkspaceActionKind.CaptureFirstPane, actions[create + 1].Kind);
        Assert.Equal(WorkspaceActionKind.WaitForReadiness, actions[create + 2].Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(250), Assert.IsType<WorkspaceAction<TimeSpan>>(actions[create + 2]).Request);
        Assert.Equal(WorkspaceActionKind.CloseReadinessChannel, actions[create + 3].Kind);
        Assert.Equal(2, actions.Count(action => action.Kind == WorkspaceActionKind.OpenReadinessChannel));
        Assert.Equal(2, plan.CompensationActions.Count(action => action.Kind == WorkspaceActionKind.CloseReadinessChannel));
        await Assert.ThrowsAsync<WorkspaceFormatException>(() => builder.PlanAsync(workspace.WithDefaults(
            environment: new Dictionary<string, string> { ["LIBTMUX_WORKSPACE_READY"] = "unowned" }), options, token));
        Assert.Null(await scope.Server.InspectAsync(token));
    }

    [Fact]
    public async Task Invalid_wait_budgets_are_rejected_before_inspection()
    {
        Server absent = Server.Open(new ServerConnectionOptions { TmuxBinaryPath = "/missing-workspace-tmux" });
        WorkspaceFile workspace = new("planned", windows: [new WorkspaceWindow()]);
        WorkspaceBuilder builder = new(absent);
        foreach (WorkspacePlanOptions options in new WorkspacePlanOptions[]
        {
            new() { ReadinessTimeout = TimeSpan.MaxValue },
            new() { HostScriptTimeout = TimeSpan.MaxValue },
            new() { MaxHostOutputBytes = 0 },
        })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceBuilder.Validate(workspace, options));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => builder.PlanAsync(workspace, options,
                TestContext.Current.CancellationToken));
        }
    }

    [UnixFact]
    public async Task Host_scripts_are_explicit_and_pane_options_survive_planning()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        WorkspaceFile declaration = new("planned", startDirectory: "session-directory",
            options: new Dictionary<string, string> { ["@session"] = "reviewed" }, windows:
            [new WorkspaceWindow(panes: [new WorkspacePane(startDirectory: "pane-directory",
                options: new Dictionary<string, string> { ["@pane"] = "reviewed" })])],
            beforeScript: "printf reviewed");
        WorkspaceBuilder builder = new(scope.Server);
        await Assert.ThrowsAsync<WorkspaceFormatException>(() => builder.PlanAsync(declaration, cancellationToken: token));
        await Assert.ThrowsAsync<WorkspaceFormatException>(() => builder.PlanAsync(declaration,
            new() { AllowHostScripts = true }, token));
        WorkspaceFile resolved = declaration.Resolve(Path.GetTempPath());
        WorkspacePlan plan = await builder.PlanAsync(resolved, new() { AllowHostScripts = true }, token);
        Assert.Equal(WorkspaceActionKind.CreateSession, plan.Actions[0].Kind);
        Assert.Equal(WorkspaceActionKind.CaptureBootstrap, plan.Actions[1].Kind);
        WorkspaceAction<WorkspaceHostCommand> host = Assert.IsType<WorkspaceAction<WorkspaceHostCommand>>(plan.Actions[2]);
        Assert.Equal(WorkspaceActionKind.RunHostScript, host.Kind);
        Assert.Equal(resolved.StartDirectory, host.Request.WorkingDirectory);
        Assert.NotEqual(resolved.DocumentDirectory, host.Request.WorkingDirectory);
        Assert.NotEqual(Assert.IsType<WorkspaceAction<NewSessionRequest>>(plan.Actions[0]).Request.StartDirectory,
            host.Request.WorkingDirectory);
        WorkspaceAction<SetOptionRequest> sessionOption = Assert.IsType<WorkspaceAction<SetOptionRequest>>(plan.Actions[3]);
        Assert.Equal("session", sessionOption.Target);
        Assert.Equal("@session", sessionOption.Request.Name);
        WorkspaceAction<SetOptionRequest> paneOption = Assert.Single(plan.Actions.OfType<WorkspaceAction<SetOptionRequest>>(),
            action => action.Target == "window:0/pane:0");
        Assert.Equal("@pane", paneOption.Request.Name);
        Assert.Equal("reviewed", paneOption.Request.Value);
        Assert.Null(await scope.Server.InspectAsync(token));

        _ = await scope.Server.CreateSessionAsync(new() { Name = "planned", Command = "exec /bin/cat" }, token);
        WorkspacePlan reused = await builder.PlanAsync(declaration, new() { ExistingSession = WorkspaceExistingSession.Reuse }, token);
        Assert.Equal(WorkspaceActionKind.ReuseSession, Assert.Single(reused.Actions).Kind);
        WorkspacePlan appended = await builder.PlanAsync(resolved,
            new() { ExistingSession = WorkspaceExistingSession.Append, AllowHostScripts = true }, token);
        Assert.Equal(WorkspaceActionKind.RunHostScript, appended.Actions[0].Kind);
        Assert.Equal(WorkspaceActionKind.CreateWindow, appended.Actions[1].Kind);
        WorkspacePlan replaced = await builder.PlanAsync(resolved,
            new() { ExistingSession = WorkspaceExistingSession.Replace, AllowHostScripts = true }, token);
        int sessionCreate = replaced.Actions.ToList().FindIndex(action =>
            action.Kind == WorkspaceActionKind.CreateSession && action.Target == "session");
        Assert.True(sessionCreate > 0);
        Assert.Equal(WorkspaceActionKind.RemoveSession, replaced.Actions[sessionCreate - 1].Kind);
        Assert.Equal(WorkspaceActionKind.CaptureBootstrap, replaced.Actions[sessionCreate + 1].Kind);
        Assert.Equal(WorkspaceActionKind.RunHostScript, replaced.Actions[sessionCreate + 2].Kind);
        Assert.Equal(WorkspaceActionKind.SetOption, replaced.Actions[sessionCreate + 3].Kind);
    }

    [UnixFact]
    public async Task Append_compensation_closes_all_waits_before_removing_windows()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        _ = await scope.Server.CreateSessionAsync(new() { Name = "planned", Command = "exec /bin/cat" }, token);
        WorkspaceFile workspace = new("planned", windows: [new WorkspaceWindow(), new WorkspaceWindow()]);
        WorkspacePlan plan = await new WorkspaceBuilder(scope.Server).PlanAsync(workspace, new()
        {
            ExistingSession = WorkspaceExistingSession.Append,
            Readiness = WorkspaceReadiness.Cooperative,
            CompensateOnFailure = true,
        }, token);
        Assert.Equal(new[]
        {
            WorkspaceActionKind.CloseReadinessChannel, WorkspaceActionKind.CloseReadinessChannel,
            WorkspaceActionKind.UnlinkWindow, WorkspaceActionKind.UnlinkWindow,
        }, plan.CompensationActions.Select(action => action.Kind));
    }

    [UnixFact]
    public async Task Version_sensitive_layouts_are_checked_before_planning_workspace_actions()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        _ = await scope.Server.CreateSessionAsync(new() { Name = "sentinel", Command = "exec /bin/cat" }, token);
        Server observed = Assert.IsType<Server>(await scope.Server.InspectAsync(token));
        WorkspaceFile workspace = new("planned", windows:
            [new WorkspaceWindow(layout: "main-h", panes: [new WorkspacePane(), new WorkspacePane()])]);
        WorkspaceBuilder.Validate(workspace);

        if (observed.DaemonVersion!.Value.CompareTo(TmuxVersion.Parse("3.5")) >= 0)
        {
            await Assert.ThrowsAsync<WorkspaceFormatException>(() =>
                new WorkspaceBuilder(scope.Server).PlanAsync(workspace, cancellationToken: token));
        }
        else
        {
            WorkspacePlan plan = await new WorkspaceBuilder(scope.Server).PlanAsync(workspace, cancellationToken: token);
            Assert.Contains(plan.Actions, action => action.Kind == WorkspaceActionKind.SelectLayout);
        }

        Assert.Equal("sentinel", Assert.Single(await scope.Server.GetSessionsAsync(token)).Name);
    }

    private static TmuxTestOptions Options() => new(new ServerConnectionOptions
    {
        TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
        SocketName = $"ltwp-{Guid.NewGuid():N}"[..20],
        ConfigurationFile = "/dev/null",
        ChildEnvironment = new Dictionary<string, string?> { ["SHELL"] = "/bin/sh", ["ENV"] = null, ["BASH_ENV"] = null },
    });
}
