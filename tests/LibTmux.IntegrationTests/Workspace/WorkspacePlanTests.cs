using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Testing;
using LibTmux.Workspace;

namespace LibTmux.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class WorkspacePlanTests
{
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
        Assert.Equal("echo reviewed", Assert.Single(plan.Actions.OfType<WorkspaceAction<string>>(),
            action => action.Kind == WorkspaceActionKind.SendText).Request);
        Assert.Throws<NotSupportedException>(() => ((IList<WorkspaceAction>)plan.Actions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)create.Request.Environment).Clear());
        Assert.NotEmpty(string.Join('\n', plan.Actions));
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
        WorkspaceFile declaration = new("planned", windows:
        [new WorkspaceWindow(panes: [new WorkspacePane(options: new Dictionary<string, string> { ["@pane"] = "reviewed" })])],
            beforeScript: "printf reviewed");
        WorkspaceBuilder builder = new(scope.Server);
        await Assert.ThrowsAsync<WorkspaceFormatException>(() => builder.PlanAsync(declaration, cancellationToken: token));
        await Assert.ThrowsAsync<WorkspaceFormatException>(() => builder.PlanAsync(declaration,
            new() { AllowHostScripts = true }, token));
        WorkspaceFile resolved = declaration.Resolve(Path.GetTempPath());
        WorkspacePlan plan = await builder.PlanAsync(resolved, new() { AllowHostScripts = true }, token);
        Assert.Equal(WorkspaceActionKind.RunHostScript, plan.Actions[0].Kind);
        WorkspaceAction<SetOptionRequest> option = Assert.Single(plan.Actions.OfType<WorkspaceAction<SetOptionRequest>>());
        Assert.Equal("window:0/pane:0", option.Target);
        Assert.Equal("@pane", option.Request.Name);
        Assert.Equal("reviewed", option.Request.Value);
        Assert.Null(await scope.Server.InspectAsync(token));

        _ = await scope.Server.CreateSessionAsync(new() { Name = "planned", Command = "exec /bin/cat" }, token);
        WorkspacePlan reused = await builder.PlanAsync(declaration, new() { ExistingSession = WorkspaceExistingSession.Reuse }, token);
        Assert.Equal(WorkspaceActionKind.ReuseSession, Assert.Single(reused.Actions).Kind);
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

    private static TmuxTestOptions Options() => new(new ServerConnectionOptions
    {
        TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
        SocketName = $"ltwp-{Guid.NewGuid():N}"[..20],
        ConfigurationFile = "/dev/null",
        ChildEnvironment = new Dictionary<string, string?> { ["SHELL"] = "/bin/sh", ["ENV"] = null, ["BASH_ENV"] = null },
    });
}
