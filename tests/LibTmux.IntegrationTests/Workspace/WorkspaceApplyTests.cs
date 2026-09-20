using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Testing;
using LibTmux.Workspace;

namespace LibTmux.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class WorkspaceApplyTests
{
    [UnixFact]
    public async Task Application_uses_reviewed_actions_and_native_created_identities()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        WorkspaceFile workspace = new("planned", options: new Dictionary<string, string>
        {
            ["default-command"] = "exec /bin/cat",
            ["base-index"] = "4",
        }, windows:
        [
            new WorkspaceWindow("editor", focus: true, panes:
            [new WorkspacePane(options: new Dictionary<string, string> { ["@role"] = "editor" }), new WorkspacePane(focus: true)]),
            new WorkspaceWindow("worker"),
        ]);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(workspace, cancellationToken: token);
        WorkspaceResult result = await builder.ApplyAsync(plan, token);

        Assert.Equal(plan.Actions, result.Journal.Select(outcome => outcome.Action));
        Assert.All(result.Journal, outcome => Assert.Equal(WorkspaceActionState.Completed, outcome.State));
        Assert.All(result.CompensationJournal, outcome => Assert.Equal(WorkspaceActionState.NotStarted, outcome.State));
        Assert.Equal([4, 5], result.Windows.Select(window => window.Index));
        Session captured = result.Session;
        Assert.Same(captured, result.Windows[0].Session);
        Assert.Equal(2, captured.Windows.Count);
        Assert.Equal(result.Windows[0].Id, captured.ActiveWindow.Value.Id);
        IReadOnlyList<Pane> panes = await result.Windows[0].GetPanesAsync(token);
        Assert.Equal(2, panes.Count);
        Assert.Equal("editor", (await panes[0].Options.GetAsync(new("@role"), token)).Single().Value.Raw);
        Assert.Equal(panes[1].Id, (await result.Windows[0].RefreshAsync(token)).ActivePane.Value.Id);
        Assert.Equal(2, (await result.Session.GetWindowsAsync(token)).Count);
        Assert.Throws<NotSupportedException>(() => ((IList<WorkspaceActionOutcome>)result.Journal).Clear());
    }

    [UnixFact]
    public async Task Append_failure_compensates_only_created_placements()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        Session existing = await scope.Server.CreateSessionAsync(new() { Name = "planned", Command = "exec /bin/cat" }, token);
        Window original = (await existing.GetWindowsAsync(token)).Single();
        WorkspaceFile workspace = new("planned", windows:
        [new WorkspaceWindow(panes: [new WorkspacePane(options: new Dictionary<string, string> { ["libtmux-invalid-option"] = "fail" })])]);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(workspace, new()
        {
            ExistingSession = WorkspaceExistingSession.Append,
            CompensateOnFailure = true,
        }, token);
        WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, token));

        Assert.NotNull(failure.PartialResult);
        Assert.Equal(existing.Id, failure.PartialResult.Session.Id);
        Assert.Equal(original.Id, (await existing.GetWindowsAsync(token)).Single().Id);
        Assert.Equal(plan.Actions, failure.Journal.Select(outcome => outcome.Action));
        Assert.Contains(failure.Journal, outcome => outcome.Action.Kind == WorkspaceActionKind.CreateWindow && outcome.State == WorkspaceActionState.Completed);
        Assert.NotEqual(TmuxDispatchState.NotDispatched, failure.Dispatch);
        Assert.Equal(WorkspaceActionState.Completed, Assert.Single(failure.CompensationJournal).State);
    }

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acknowledged_bootstrap_is_compensated_when_its_first_readback_fails(bool replace)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        bool applying = false;
        bool failReadback = false;
        var readFailure = new LibTmuxException("The bootstrap readback failed.", TmuxDispatchState.NotDispatched);
        TmuxInterceptor interceptor = async (invocation, next, cancellation) =>
        {
            if (failReadback && invocation.Arguments.Contains("list-windows", StringComparer.Ordinal))
            {
                failReadback = false;
                throw readFailure;
            }
            TmuxCommandResult result = await next(cancellation);
            if (applying && invocation.Arguments.Contains("new-session", StringComparer.Ordinal))
                failReadback = true;
            return result;
        };
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(interceptor), token);
        Session original = await scope.Server.CreateSessionAsync(new()
        {
            Name = replace ? "planned" : "anchor",
            Command = "exec /bin/cat",
        }, token);
        Window originalWindow = Assert.Single(await original.GetWindowsAsync(token));
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(new("planned", windows: [new WorkspaceWindow()]), new()
        {
            ExistingSession = replace ? WorkspaceExistingSession.Replace : WorkspaceExistingSession.Error,
            CompensateOnFailure = !replace,
        }, token);
        applying = true;
        WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, token));

        Assert.Same(readFailure, failure.InnerException);
        Assert.Contains(failure.Journal, outcome => outcome.Action.Kind == WorkspaceActionKind.CreateSession
            && outcome.State == WorkspaceActionState.Completed);
        string bootstrap = replace ? "keepalive-bootstrap" : "bootstrap";
        WorkspaceActionOutcome cleanup = Assert.Single(failure.CompensationJournal, outcome => outcome.Action.Target == bootstrap);
        Assert.Equal(WorkspaceActionState.Completed, cleanup.State);
        Assert.Equal(original.Id, Assert.Single(await scope.Server.GetSessionsAsync(token)).Id);
        Assert.Equal(originalWindow.Id, Assert.Single(await original.GetWindowsAsync(token)).Id);
    }

    [UnixFact]
    public async Task Reuse_is_inert_and_replacement_preserves_the_reviewed_daemon()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        Session existing = await scope.Server.CreateSessionAsync(new() { Name = "planned", Command = "exec /bin/cat" }, token);
        WorkspaceFile declaration = new("planned", options: new Dictionary<string, string> { ["default-command"] = "exec /bin/cat" },
            windows: [new WorkspaceWindow("replacement")]);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan reused = await builder.PlanAsync(declaration, new() { ExistingSession = WorkspaceExistingSession.Reuse }, token);
        WorkspaceResult reuse = await builder.ApplyAsync(reused, token);
        Assert.Equal(existing.Id, reuse.Session.Id);
        Assert.Empty(reuse.Windows);
        Assert.Equal(WorkspaceActionKind.ReuseSession, Assert.Single(reuse.Journal).Action.Kind);

        WorkspacePlan replace = await builder.PlanAsync(declaration, new() { ExistingSession = WorkspaceExistingSession.Replace }, token);
        WorkspaceResult replaced = await builder.ApplyAsync(replace, token);
        Assert.Equal(existing.Generation, replaced.Session.Generation);
        Assert.NotEqual(existing.Id, replaced.Session.Id);
        Assert.Equal(replaced.Session.Id, Assert.Single(await scope.Server.GetSessionsAsync(token)).Id);
        Assert.Equal("replacement", Assert.Single(replaced.Windows).Name);
    }

    [UnixFact]
    public async Task Changed_session_name_refuses_replacement_before_any_action()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        Session existing = await scope.Server.CreateSessionAsync(new() { Name = "planned", Command = "exec /bin/cat" }, token);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(new("planned", windows: [new WorkspaceWindow()]),
            new() { ExistingSession = WorkspaceExistingSession.Replace, CompensateOnFailure = true }, token);
        _ = await existing.RenameAsync("changed", token);
        WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, token));
        Assert.Equal(TmuxDispatchState.NotDispatched, failure.Dispatch);
        Assert.All(failure.Journal, outcome => Assert.Equal(WorkspaceActionState.NotStarted, outcome.State));
        Assert.Equal(existing.Id, Assert.Single(await scope.Server.GetSessionsAsync(token)).Id);
    }

    [UnixFact]
    public async Task Workspace_names_are_literal_and_conflicts_use_the_same_names()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        await scope.Server.CreateSessionAsync(new() { Name = "anchor", Command = "exec /bin/cat" }, token);
        const string name = "workspace-#{session_name}-#[bold]-##[literal]";
        WorkspaceFile declaration = new(name,
            options: new Dictionary<string, string> { ["default-command"] = "exec /bin/cat" },
            windows: [new WorkspaceWindow(name)]);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(declaration, cancellationToken: token);
        WorkspaceResult created = await builder.ApplyAsync(plan, token);

        Assert.Equal(name, created.Session.Name);
        Assert.Equal(name, Assert.Single(created.Windows).Name);
        WorkspacePlan reuse = await builder.PlanAsync(declaration, new() { ExistingSession = WorkspaceExistingSession.Reuse }, token);
        Assert.Equal(WorkspaceActionKind.ReuseSession, Assert.Single(reuse.Actions).Kind);
        WorkspaceBuildException conflict = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, token));
        Assert.Equal(TmuxDispatchState.NotDispatched, conflict.Dispatch);
        Assert.All(conflict.Journal, outcome => Assert.Equal(WorkspaceActionState.NotStarted, outcome.State));
    }

    [UnixFact]
    public async Task Unknown_creation_is_reported_without_name_based_compensation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        bool loseReply = false;
        TmuxInterceptor interceptor = async (invocation, next, cancellation) =>
        {
            TmuxCommandResult result = await next(cancellation);
            if (loseReply && invocation.Arguments.Contains("new-session", StringComparer.Ordinal))
            {
                loseReply = false;
                throw new LibTmuxException("The creation reply was lost.", TmuxDispatchState.Unknown);
            }
            return result;
        };
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(interceptor), token);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(new("planned", windows: [new WorkspaceWindow()]),
            new() { CompensateOnFailure = true }, token);
        loseReply = true;
        WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, token));
        Assert.Null(failure.PartialResult);
        Assert.Equal(WorkspaceActionState.Unknown, failure.Journal[0].State);
        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.All(failure.CompensationJournal, outcome => Assert.Equal(WorkspaceActionState.NotStarted, outcome.State));
        Assert.Equal("planned", Assert.Single(await scope.Server.GetSessionsAsync(token)).Name);
    }

    [UnixFact]
    public async Task A_substituted_initial_pane_never_receives_workspace_input()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Server? actor = null;
        Pane? foreign = null;
        bool swap = false;
        TmuxInterceptor interceptor = async (invocation, next, cancellation) =>
        {
            TmuxCommandResult result = await next(cancellation);
            if (swap && invocation.Arguments.Contains("new-window", StringComparer.Ordinal))
            {
                swap = false;
                Window created = (await actor!.GetWindowsAsync(cancellation)).Single(window => window.Name == "created");
                Pane initial = (await created.GetPanesAsync(cancellation)).Single();
                await initial.SwapAsync(new() { Target = foreign!.Id.ToString(), Detach = true }, cancellation);
            }
            return result;
        };
        TmuxTestOptions options = Options(interceptor);
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(options, token);
        actor = Server.Open(scope.Server.ConnectionOptions with { Interceptor = null });
        Session origin = await actor.CreateSessionAsync(new() { Name = "origin", Command = "exec /bin/cat" }, token);
        foreign = (await origin.GetPanesAsync(token)).Single();
        WorkspaceFile declaration = new("planned", options: new Dictionary<string, string> { ["default-command"] = "exec /bin/cat" },
            windows: [new WorkspaceWindow("created", panes: [new WorkspacePane(["must-not-send"])])]);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(declaration, new() { CompensateOnFailure = true }, token);
        swap = true;
        WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, token));

        Assert.False(swap);
        Assert.Contains(failure.Journal, outcome => outcome.Action.Kind == WorkspaceActionKind.CaptureFirstPane && outcome.Failure is not null);
        Assert.All(failure.Journal.Where(outcome => outcome.Action.Kind == WorkspaceActionKind.SendText),
            outcome => Assert.Equal(WorkspaceActionState.NotStarted, outcome.State));
        Assert.Equal(foreign.Id, (await actor.GetPaneAsync(foreign.Id, token)).Id);
        Assert.Contains(failure.CompensationJournal, outcome => outcome.Action.Target == "window:0" && outcome.Failure is not null);
    }

    [UnixFact]
    public async Task Cleanup_refuses_a_foreign_pane_inserted_after_its_membership_read()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Server? actor = null;
        Pane? foreign = null;
        bool insert = false;
        TmuxInterceptor interceptor = async (invocation, next, cancellation) =>
        {
            if (insert && invocation.Arguments.Contains("unlink-window", StringComparer.Ordinal))
            {
                insert = false;
                Window created = (await actor!.GetWindowsAsync(cancellation)).Single(window => window.Name == "created");
                Pane initial = (await created.GetPanesAsync(cancellation)).Single();
                await foreign!.MoveAsync(new(initial.Id.ToString()), cancellation);
            }
            return await next(cancellation);
        };
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(interceptor), token);
        actor = Server.Open(scope.Server.ConnectionOptions with { Interceptor = null });
        Session existing = await actor.CreateSessionAsync(new() { Name = "planned", Command = "exec /bin/cat" }, token);
        Pane seed = (await existing.GetPanesAsync(token)).Single();
        foreign = await seed.SplitAsync(new() { Command = "exec /bin/cat" }, token);
        WorkspaceFile declaration = new("planned", windows:
        [new WorkspaceWindow("created", panes: [new WorkspacePane(options: new Dictionary<string, string> { ["libtmux-invalid-option"] = "fail" })])]);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(declaration, new()
        {
            ExistingSession = WorkspaceExistingSession.Append,
            CompensateOnFailure = true,
        }, token);
        insert = true;
        WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, token));

        Assert.False(insert);
        Assert.NotNull(Assert.Single(failure.CompensationJournal).Failure);
        Assert.Equal(foreign.Id, (await actor.GetPaneAsync(foreign.Id, token)).Id);
        Assert.Equal(2, (await existing.GetWindowsAsync(token)).Count);
    }

    [UnixFact]
    public async Task Cooperative_startup_signals_before_wait_and_each_application_owns_fresh_channels()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        Session existing = await scope.Server.CreateSessionAsync(new() { Name = "planned", Command = "exec /bin/cat" }, token);
        string binary = ShellQuote(Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux");
        await existing.Options.SetAsync(new("default-command", $"{binary} wait-for -S \"$LIBTMUX_WORKSPACE_READY\"; exec /bin/cat"), token);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(new("planned", windows:
            [new WorkspaceWindow(panes: [new WorkspacePane(), new WorkspacePane()])]), new()
            {
                ExistingSession = WorkspaceExistingSession.Append,
                Readiness = WorkspaceReadiness.Cooperative,
                ReadinessTimeout = TimeSpan.FromSeconds(1),
            }, token);

        WorkspaceResult first = await builder.ApplyAsync(plan, token);
        WorkspaceResult second = await builder.ApplyAsync(plan, token);
        WorkspaceActionOutcome[] opens = [.. first.Journal.Concat(second.Journal)
            .Where(outcome => outcome.Action.Kind == WorkspaceActionKind.OpenReadinessChannel)];
        Assert.Equal(4, opens.Length);
        Assert.Equal(4, opens.Select(outcome => Assert.IsType<string>(outcome.Result)).Distinct(StringComparer.Ordinal).Count());
        Assert.All(first.Journal.Concat(second.Journal), outcome => Assert.Equal(WorkspaceActionState.Completed, outcome.State));
        Assert.NotEqual(first.Windows[0].Id, second.Windows[0].Id);
        Assert.Equal(3, (await existing.GetWindowsAsync(token)).Count);
    }

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Split_refuses_a_moved_source_and_retains_a_moved_result(bool moveSource)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Server? actor = null;
        Pane? foreign = null;
        bool move = false;
        TmuxInterceptor interceptor = async (invocation, next, cancellation) =>
        {
            if (!move || !invocation.Arguments.Contains("split-window", StringComparer.Ordinal))
                return await next(cancellation);
            move = false;
            Window created = (await actor!.GetWindowsAsync(cancellation)).Single(window => window.Name == "created");
            Pane initial = (await created.GetPanesAsync(cancellation)).Single();
            if (moveSource)
                await initial.MoveAsync(new(foreign!.Id.ToString()), cancellation);
            TmuxCommandResult result = await next(cancellation);
            if (!moveSource)
            {
                Pane added = (await created.GetPanesAsync(cancellation)).Single(pane => pane.Id != initial.Id);
                await added.MoveAsync(new(foreign!.Id.ToString()), cancellation);
            }
            return result;
        };
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(interceptor), token);
        actor = Server.Open(scope.Server.ConnectionOptions with { Interceptor = null });
        Session anchor = await actor.CreateSessionAsync(new() { Name = "anchor", Command = "exec /bin/cat" }, token);
        foreign = (await anchor.GetPanesAsync(token)).Single();
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(new("planned",
            options: new Dictionary<string, string> { ["default-command"] = "exec /bin/cat" },
            windows: [new WorkspaceWindow("created", panes: [new WorkspacePane(), new WorkspacePane()])]),
            new() { CompensateOnFailure = true }, token);
        move = true;
        WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, token));

        Assert.False(move);
        Assert.Equal(2, (await anchor.GetPanesAsync(token)).Count);
        WorkspaceActionOutcome split = Assert.Single(failure.Journal, outcome => outcome.Action.Kind == WorkspaceActionKind.SplitPane);
        Assert.NotNull(split.Failure);
        if (moveSource)
            Assert.Null(split.Result);
        else
        {
            Assert.Equal(WorkspaceActionState.Unknown, split.State);
            Pane made = Assert.IsType<Pane>(split.Result);
            Assert.Equal(foreign.Window.Id, made.Window.Id);
            Assert.Contains(await anchor.GetPanesAsync(token), pane => pane.Id == made.Id);
        }
    }

    [UnixFact]
    public async Task Cancellation_between_actions_leaves_the_next_action_not_started()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using CancellationTokenSource application = CancellationTokenSource.CreateLinkedTokenSource(token);
        int creations = 0;
        TmuxInterceptor interceptor = (invocation, next, cancellation) =>
        {
            if (invocation.Arguments.Contains("wait-for", StringComparer.Ordinal)
                && !invocation.Arguments.Contains("-S", StringComparer.Ordinal))
                application.Cancel();
            if (invocation.Arguments.Contains("new-window", StringComparer.Ordinal))
                creations++;
            return next(cancellation);
        };
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(interceptor), token);
        Session existing = await scope.Server.CreateSessionAsync(new() { Name = "planned", Command = "exec /bin/cat" }, token);
        Window original = Assert.Single(await existing.GetWindowsAsync(token));
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(new("planned", windows: [new WorkspaceWindow()]), new()
        {
            ExistingSession = WorkspaceExistingSession.Append,
            Readiness = WorkspaceReadiness.Cooperative,
            CompensateOnFailure = true,
        }, token);

        WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, application.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.Equal(WorkspaceActionKind.OpenReadinessChannel, failure.Journal[0].Action.Kind);
        Assert.Equal(WorkspaceActionState.Completed, failure.Journal[0].State);
        Assert.Equal(0, creations);
        Assert.All(failure.Journal.Skip(1), outcome => Assert.Equal(WorkspaceActionState.NotStarted, outcome.State));
        Assert.Equal(WorkspaceActionState.Completed, Assert.Single(failure.CompensationJournal,
            outcome => outcome.Action.Kind == WorkspaceActionKind.CloseReadinessChannel).State);
        Assert.Equal(original.Id, Assert.Single(await existing.GetWindowsAsync(token)).Id);
    }

    [UnixFact]
    public async Task Missing_cooperative_signal_sends_no_input_and_cleans_owned_resources()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
        Session anchor = await scope.Server.CreateSessionAsync(new() { Name = "anchor", Command = "exec /bin/cat" }, token);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(new("planned",
            options: new Dictionary<string, string> { ["default-command"] = "exec /bin/cat" },
            windows: [new WorkspaceWindow(panes: [new WorkspacePane(["must-not-send"])])]), new()
            {
                Readiness = WorkspaceReadiness.Cooperative,
                ReadinessTimeout = TimeSpan.FromMilliseconds(100),
                CompensateOnFailure = true,
            }, token);
        WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, token));

        Assert.IsType<TmuxWaitTimeoutException>(failure.InnerException);
        Assert.All(failure.Journal.Where(outcome => outcome.Action.Kind == WorkspaceActionKind.SendText),
            outcome => Assert.Equal(WorkspaceActionState.NotStarted, outcome.State));
        Assert.All(failure.CompensationJournal, outcome => Assert.Equal(WorkspaceActionState.Completed, outcome.State));
        Assert.Equal(anchor.Id, Assert.Single(await scope.Server.GetSessionsAsync(token)).Id);
    }

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Host_action_output_and_failure_are_retained_in_the_reviewed_journal(int exitCode)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string directory = Directory.CreateTempSubdirectory("libtmux-workspace-host-journal-").FullName;
        try
        {
            await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(), token);
            Session anchor = await scope.Server.CreateSessionAsync(new() { Name = "anchor", Command = "exec /bin/cat" }, token);
            WorkspaceFile declaration = new WorkspaceFile("planned",
                options: new Dictionary<string, string> { ["default-command"] = "exec /bin/cat" },
                windows: [new WorkspaceWindow()],
                beforeScript: $"printf '%s:%s' \"$MARK\" \"$PWD\"; printf diagnostic >&2; exit {exitCode}")
                .WithDefaults(environment: new Dictionary<string, string> { ["MARK"] = "declared" }).Resolve(directory);
            WorkspaceBuilder builder = new(scope.Server);
            WorkspacePlan plan = await builder.PlanAsync(declaration, new() { AllowHostScripts = true }, token);
            Assert.Equal(anchor.Id, Assert.Single(await scope.Server.GetSessionsAsync(token)).Id);
            IReadOnlyList<WorkspaceActionOutcome> journal;
            if (exitCode == 0)
            {
                WorkspaceResult result = await builder.ApplyAsync(plan, token);
                journal = result.Journal;
                Assert.All(journal, outcome => Assert.Equal(WorkspaceActionState.Completed, outcome.State));
            }
            else
            {
                WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, token));
                journal = failure.Journal;
                Assert.Null(failure.PartialResult);
                Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
                Assert.Equal(WorkspaceActionState.Unknown, journal[0].State);
                Assert.All(journal.Skip(1), outcome => Assert.Equal(WorkspaceActionState.NotStarted, outcome.State));
                Assert.Equal(anchor.Id, Assert.Single(await scope.Server.GetSessionsAsync(token)).Id);
            }
            Assert.Same(plan.Actions[0], journal[0].Action);
            WorkspaceHostResult host = Assert.IsType<WorkspaceHostResult>(journal[0].Result);
            Assert.True(host.Started);
            Assert.Equal(exitCode, host.ExitCode);
            Assert.Equal($"declared:{directory}", host.StandardOutput);
            Assert.Equal("diagnostic", host.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [UnixFact]
    public async Task Failed_channel_cleanup_preserves_the_readiness_cause_and_remote_uncertainty()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TmuxInterceptor interceptor = async (invocation, next, cancellation) =>
        {
            if (invocation.Arguments.Contains("wait-for", StringComparer.Ordinal)
                && invocation.Arguments.Contains("-S", StringComparer.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return await next(cancellation);
        };
        await using TemporaryServerScope scope = await new TmuxTestFactory().CreateServerAsync(Options(interceptor), token);
        await scope.Server.CreateSessionAsync(new() { Name = "anchor", Command = "exec /bin/cat" }, token);
        WorkspaceBuilder builder = new(scope.Server);
        WorkspacePlan plan = await builder.PlanAsync(new("planned",
            options: new Dictionary<string, string> { ["default-command"] = "exec /bin/cat" },
            windows: [new WorkspaceWindow()]), new()
            {
                Readiness = WorkspaceReadiness.Cooperative,
                ReadinessTimeout = TimeSpan.FromMilliseconds(100),
                CleanupTimeout = TimeSpan.FromMilliseconds(100),
                CompensateOnFailure = true,
            }, token);
        WorkspaceBuildException failure = await Assert.ThrowsAsync<WorkspaceBuildException>(() => builder.ApplyAsync(plan, token));

        Assert.IsType<TmuxWaitTimeoutException>(failure.InnerException);
        WorkspaceActionOutcome close = Assert.Single(failure.CompensationJournal,
            outcome => outcome.Action.Kind == WorkspaceActionKind.CloseReadinessChannel);
        Assert.Equal(WorkspaceActionState.Unknown, close.State);
        Assert.Equal(TmuxDispatchState.Unknown, close.Dispatch);
        Assert.NotNull(close.Failure);
        Assert.All(failure.CompensationJournal.Where(outcome => outcome.Action.Kind == WorkspaceActionKind.UnlinkWindow),
            outcome => Assert.Equal(TmuxDispatchState.NotDispatched, outcome.Dispatch));
        Assert.Equal(2, (await scope.Server.GetSessionsAsync(token)).Count);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static TmuxTestOptions Options(TmuxInterceptor? interceptor = null) => new(new ServerConnectionOptions
    {
        TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
        SocketName = $"ltwa-{Guid.NewGuid():N}"[..20],
        ConfigurationFile = "/dev/null",
        ChildEnvironment = new Dictionary<string, string?> { ["SHELL"] = "/bin/sh", ["ENV"] = null, ["BASH_ENV"] = null },
        Interceptor = interceptor,
    });
}
