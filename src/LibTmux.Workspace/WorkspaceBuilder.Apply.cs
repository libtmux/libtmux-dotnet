namespace LibTmux.Workspace;

public sealed partial class WorkspaceBuilder
{
    /// <summary>Applies the reviewed actions and records their individual outcomes.</summary>
    /// <param name="plan">The immutable plan for this builder's resolved endpoint.</param>
    /// <param name="cancellationToken">Stops further application and cancels the current operation.</param>
    /// <returns>The materialized session, created windows and complete action journal.</returns>
    /// <exception cref="ArgumentException">The plan selects another endpoint.</exception>
    /// <exception cref="WorkspaceBuildException">Preconditions, application or owned cleanup failed.</exception>
    /// <remarks>
    /// Native defaults and hooks remain runtime dependencies. Input acknowledgement is
    /// not program completion. Cleanup uses only returned native identities; unknown
    /// creations and irreversible shell effects remain visible in the journal.
    /// </remarks>
    public async Task<WorkspaceResult> ApplyAsync(WorkspacePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_server.Equals(plan.Endpoint))
            throw new ArgumentException("The workspace plan selects a different tmux endpoint.", nameof(plan));
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceApplyState state = new(_server, plan.Actions);
        WorkspaceActionOutcome[] journal = InitialJournal(plan.Actions);
        WorkspaceActionOutcome[] cleanup = InitialJournal(plan.CompensationActions);
        int current = -1;
        try
        {
            await VerifyWorkspacePlanAsync(plan, state, cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < plan.Actions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = index;
                WorkspaceAction action = plan.Actions[current];
                try
                {
                    object? result = await ApplyActionAsync(action, state, cancellationToken).ConfigureAwait(false);
                    journal[current] = new(action, WorkspaceActionState.Completed, CompletedDispatch(action), result);
                }
                catch (LibTmuxException failure) when (action.Kind == WorkspaceActionKind.SelectLayout
                    && failure is TmuxWindowException or TmuxCommandException)
                {
                    state.Unsupported.Add($"Layout for '{action.Target}' was rejected: {failure.Message}");
                    journal[current] = new(action, WorkspaceActionState.Rejected, failure.Dispatch, failure: failure);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return state.Result(journal, cleanup)
                ?? throw new InvalidOperationException("The workspace plan returned no session.");
        }
        catch (Exception failure)
        {
            if (current >= 0 && current < journal.Length && journal[current].State == WorkspaceActionState.NotStarted)
            {
                WorkspaceAction action = plan.Actions[current];
                object? materialized = action.Kind == WorkspaceActionKind.SplitPane
                    ? state.Targets.GetValueOrDefault(action.Target) : null;
                journal[current] = FailedOutcome(action, failure, materialized);
            }
            await CompensateWorkspaceAsync(plan, state, cleanup).ConfigureAwait(false);
            throw new WorkspaceBuildException(state.Result(journal, cleanup), failure, journal, cleanup);
        }
    }

    private static WorkspaceActionOutcome[] InitialJournal(IReadOnlyList<WorkspaceAction> actions) =>
        [.. actions.Select(action => new WorkspaceActionOutcome(action, WorkspaceActionState.NotStarted,
            TmuxDispatchState.NotDispatched))];

    private static TmuxDispatchState CompletedDispatch(WorkspaceAction action) => action.Kind == WorkspaceActionKind.ReuseSession
        ? TmuxDispatchState.NotDispatched : TmuxDispatchState.Dispatched;

    private static WorkspaceActionOutcome FailedOutcome(WorkspaceAction action, Exception failure, object? materialized = null)
    {
        object? result = failure switch
        {
            WorkspaceHostFailureException host => host.Result,
            WorkspaceHostCanceledException host => host.Result,
            _ => materialized,
        };
        TmuxDispatchState dispatch = failure switch
        {
            OperationCanceledException when action.Kind == WorkspaceActionKind.CloseReadinessChannel => TmuxDispatchState.Unknown,
            LibTmuxException native => native.Dispatch,
            TmuxOperationCanceledException native => native.CommandMayHaveExecuted ? TmuxDispatchState.Unknown : TmuxDispatchState.NotDispatched,
            StaleServerGenerationException => TmuxDispatchState.NotDispatched,
            OperationCanceledException when result is null => TmuxDispatchState.NotDispatched,
            _ => TmuxDispatchState.Unknown,
        };
        if (result is WorkspaceHostResult hostResult)
            dispatch = hostResult.Started ? TmuxDispatchState.Unknown : TmuxDispatchState.NotDispatched;
        return new(action, dispatch == TmuxDispatchState.Unknown ? WorkspaceActionState.Unknown : WorkspaceActionState.Failed,
            dispatch, result, failure);
    }

    private async Task VerifyWorkspacePlanAsync(WorkspacePlan plan, WorkspaceApplyState state, CancellationToken token)
    {
        Server? live = await (plan.ObservedServer ?? _server).InspectAsync(token).ConfigureAwait(false);
        if (plan.ObservedServer is not null && live is null)
            throw new LibTmuxException("The workspace's observed daemon is no longer available.", TmuxDispatchState.NotDispatched);
        if (live is null && plan.ServerStartup == WorkspaceServerStartup.RequireExisting)
            throw new LibTmuxException("The workspace plan requires an existing daemon.", TmuxDispatchState.NotDispatched);
        state.Server = live ?? _server;
        if (plan.ExistingSession is Session existing)
        {
            Session? current = await state.Server.FindSessionAsync(existing.Id, token).ConfigureAwait(false);
            if (current is null || current.Generation != existing.Generation || current.Name != existing.Name)
                throw new LibTmuxException("The workspace's reviewed session identity or name changed.", TmuxDispatchState.NotDispatched);
            state.Targets.Add("existing", current);
            if (plan.ExistingSessionPolicy is WorkspaceExistingSession.Append or WorkspaceExistingSession.Reuse)
                state.Targets.Add("session", current);
        }
        else if (live is not null)
        {
            if ((await live.GetSessionsAsync(token).ConfigureAwait(false))
                .Any(session => string.Equals(session.Name, plan.SessionName, StringComparison.Ordinal)))
                throw new LibTmuxException("The workspace session name became occupied after planning.", TmuxDispatchState.NotDispatched);
        }
    }

    private static async Task<object?> ApplyActionAsync(WorkspaceAction action, WorkspaceApplyState state, CancellationToken token)
    {
        switch (action.Kind)
        {
            case WorkspaceActionKind.RunHostScript:
                return await WorkspaceHostScript.RunAsync(((WorkspaceAction<WorkspaceHostCommand>)action).Request, token).ConfigureAwait(false);
            case WorkspaceActionKind.CreateSession:
                {
                    NewSessionRequest request = ((WorkspaceAction<NewSessionRequest>)action).Request;
                    SessionCreationResult created = await state.Server.CreateSessionWithReceiptAsync(request with
                    {
                        ExpectedGeneration = request.ExpectedGeneration ?? state.Server.Generation,
                    }, token).ConfigureAwait(false);
                    Session session = created.Session;
                    state.SessionCreations.Add(action.Target, created);
                    state.KnownPanes.Add(created.InitialWindowId, [created.InitialPaneId]);
                    foreach ((string bootstrap, string source) in state.BootstrapSources)
                    {
                        if (source == action.Target)
                            state.Owned.Add(bootstrap);
                    }
                    state.Server = session.Server;
                    state.Targets.Add(action.Target, session);
                    state.Owned.Add(action.Target);
                    return session;
                }
            case WorkspaceActionKind.CaptureBootstrap:
                {
                    SessionCreationResult created = state.SessionCreations[action.SourceTarget!];
                    Window bootstrap = await ReadBootstrapAsync(created, token).ConfigureAwait(false);
                    state.Targets.Add(action.Target, bootstrap);
                    return bootstrap;
                }
            case WorkspaceActionKind.RemoveSession:
                await state.Get<Session>(action.Target).KillAsync(cancellationToken: token).ConfigureAwait(false);
                state.Owned.Remove(action.Target);
                return null;
            case WorkspaceActionKind.CreateWindow:
                {
                    NewWindowRequest request = ((WorkspaceAction<NewWindowRequest>)action).Request;
                    string firstPane = $"{action.Target}/pane:0";
                    WindowCreationResult created = await state.Get<Session>(action.SourceTarget!).CreateWindowWithReceiptAsync(request with
                    {
                        Environment = ReadinessEnvironment(state, firstPane, request.Environment),
                    }, token).ConfigureAwait(false);
                    Window window = created.Window;
                    state.WindowCreations.Add(action.Target, created);
                    state.KnownPanes.Add(window.Id, [created.InitialPaneId]);
                    state.Targets.Add(action.Target, window);
                    state.WindowTargets.Add(action.Target);
                    state.Owned.Add(action.Target);
                    return window;
                }
            case WorkspaceActionKind.CaptureFirstPane:
                {
                    WindowCreationResult created = state.WindowCreations[action.SourceTarget!];
                    Pane pane = (await created.Window.GetPanesAsync(token).ConfigureAwait(false))
                        .Single(value => value.Id == created.InitialPaneId && value.Window.Id == created.Window.Id);
                    state.Targets.Add(action.Target, pane);
                    return pane;
                }
            case WorkspaceActionKind.SplitPane:
                {
                    SplitPaneRequest request = ((WorkspaceAction<SplitPaneRequest>)action).Request;
                    Pane source = state.Get<Pane>(action.SourceTarget!);
                    Pane pane = await source.SplitAsync(request with
                    {
                        Environment = ReadinessEnvironment(state, action.Target, request.Environment),
                        ExpectedWindowId = source.Window.Id,
                    }, token).ConfigureAwait(false);
                    state.Targets.Add(action.Target, pane);
                    if (pane.Window.Id != source.Window.Id)
                        throw new LibTmuxException("The created workspace pane moved before readback.", TmuxDispatchState.Unknown);
                    state.KnownPanes[source.Window.Id].Add(pane.Id);
                    return pane;
                }
            case WorkspaceActionKind.SetOption:
                {
                    TmuxOptions options = state.Targets[action.Target] switch
                    {
                        Session session => session.Options,
                        Window window => window.Options,
                        Pane pane => pane.Options,
                        _ => throw new InvalidOperationException("The workspace option target is invalid."),
                    };
                    await options.SetAsync(((WorkspaceAction<SetOptionRequest>)action).Request, token).ConfigureAwait(false);
                    return null;
                }
            case WorkspaceActionKind.UnlinkWindow:
                {
                    if (!state.Targets.TryGetValue(action.Target, out object? target))
                    {
                        SessionCreationResult created = state.SessionCreations[state.BootstrapSources[action.Target]];
                        target = await ReadBootstrapAsync(created, token).ConfigureAwait(false);
                        state.Targets.Add(action.Target, target);
                    }
                    Window window = (Window)target;
                    IReadOnlyList<Pane> current = await window.GetPanesAsync(token).ConfigureAwait(false);
                    HashSet<PaneId> known = state.KnownPanes[window.Id];
                    if (current.Any(pane => !known.Contains(pane.Id)))
                        throw new LibTmuxException("Workspace cleanup refused a window containing an unowned pane.", TmuxDispatchState.NotDispatched);
                    await window.UnlinkAsync(true, [.. current.Select(pane => pane.Id)], token).ConfigureAwait(false);
                    state.Owned.Remove(action.Target);
                    return null;
                }
            case WorkspaceActionKind.MoveToBaseIndex:
                {
                    string index = await ReadOptionAsync(state.Get<Session>(action.SourceTarget!).Options, "base-index", false, token).ConfigureAwait(false);
                    WindowId id = state.Get<Window>(action.Target).Id;
                    Window window = (await state.Get<Session>(action.SourceTarget!).GetWindowsAsync(token).ConfigureAwait(false))
                        .Single(candidate => candidate.Id == id);
                    if (window.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) != index)
                        window = await window.MoveAsync(new() { Destination = index, NoSelect = true }, token).ConfigureAwait(false);
                    state.Targets[action.Target] = window;
                    return window;
                }
            case WorkspaceActionKind.SendText:
                await state.Get<Pane>(action.Target).SendTextAsync(((WorkspaceAction<string>)action).Request, cancellationToken: token).ConfigureAwait(false);
                return null;
            case WorkspaceActionKind.ArrangePanes:
            case WorkspaceActionKind.SelectLayout:
                {
                    Window window = await state.Get<Window>(action.Target).SelectLayoutAsync(((WorkspaceAction<SelectLayoutRequest>)action).Request, token).ConfigureAwait(false);
                    state.Targets[action.Target] = window;
                    return window;
                }
            case WorkspaceActionKind.SelectPane:
                {
                    Pane pane = await state.Get<Pane>(action.Target).SelectAsync(cancellationToken: token).ConfigureAwait(false);
                    state.Targets[action.Target] = pane;
                    return pane;
                }
            case WorkspaceActionKind.SelectWindow:
                {
                    Window window = state.Get<Window>(action.Target);
                    Window selected = await state.Get<Session>("session").SelectWindowAsync(window.Id.ToString(), token).ConfigureAwait(false);
                    if (selected.Id != window.Id)
                        throw new LibTmuxException("The selected workspace window changed before readback.", TmuxDispatchState.Unknown);
                    state.Targets[action.Target] = selected;
                    return selected;
                }
            case WorkspaceActionKind.OpenReadinessChannel:
                {
                    TmuxWaitChannel channel = state.Server.OpenWaitChannel($"libtmux-workspace-{Guid.NewGuid():N}");
                    state.Channels.Add(action.Target, channel);
                    return channel.Channel;
                }
            case WorkspaceActionKind.WaitForReadiness:
                if (!await state.Channels[action.Target].WaitAsync(((WorkspaceAction<TimeSpan>)action).Request, token).ConfigureAwait(false))
                    throw new TmuxWaitTimeoutException("The workspace pane did not signal cooperative readiness.", ((WorkspaceAction<TimeSpan>)action).Request);
                return null;
            case WorkspaceActionKind.CloseReadinessChannel:
                await state.Channels[action.Target].CloseAsync(token).ConfigureAwait(false);
                state.Channels.Remove(action.Target);
                return null;
            case WorkspaceActionKind.ReuseSession:
                return state.Get<Session>("session");
            case WorkspaceActionKind.CaptureResult:
                {
                    Server captured = await state.Server.CaptureSnapshotAsync(SnapshotDepth.Panes, token).ConfigureAwait(false);
                    Session session = captured.Sessions.Single(candidate => candidate.Id == state.Get<Session>("session").Id);
                    Window[] windows = [.. state.WindowTargets.Select(target =>
                {
                    Window previous = state.Get<Window>(target);
                    return session.Windows.Single(window => window.Id == previous.Id && window.Index == previous.Index);
                })];
                    for (int index = 0; index < windows.Length; index++)
                        state.Targets[state.WindowTargets[index]] = windows[index];
                    state.Targets["session"] = session;
                    state.Server = captured;
                    return session;
                }
            default:
                throw new InvalidOperationException($"Workspace action '{action.Kind}' has no executor.");
        }
    }

    private static async Task<Window> ReadBootstrapAsync(SessionCreationResult created, CancellationToken token) =>
        (await created.Session.GetWindowsAsync(token).ConfigureAwait(false))
            .Single(window => window.Id == created.InitialWindowId && window.Index == created.InitialWindowIndex);

    private static IReadOnlyDictionary<string, string>? ReadinessEnvironment(WorkspaceApplyState state, string target,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (!state.Channels.TryGetValue(target, out TmuxWaitChannel? channel))
            return environment;
        Dictionary<string, string> copy = environment is null ? new(StringComparer.Ordinal) : new(environment, StringComparer.Ordinal);
        copy.Add("LIBTMUX_WORKSPACE_READY", channel.Channel);
        return copy;
    }

    private static async Task CompensateWorkspaceAsync(WorkspacePlan plan, WorkspaceApplyState state, WorkspaceActionOutcome[] journal)
    {
        using CancellationTokenSource cleanup = new(plan.CleanupTimeout);
        for (int index = 0; index < plan.CompensationActions.Count; index++)
        {
            WorkspaceAction action = plan.CompensationActions[index];
            bool available = action.Kind == WorkspaceActionKind.CloseReadinessChannel
                ? state.Channels.ContainsKey(action.Target) : state.Owned.Contains(action.Target);
            if (!available)
                continue;
            try
            {
                object? result = await ApplyActionAsync(action, state, cleanup.Token).ConfigureAwait(false);
                journal[index] = new(action, WorkspaceActionState.Completed, CompletedDispatch(action), result);
            }
            catch (Exception failure)
            {
                journal[index] = FailedOutcome(action, failure);
            }
        }
    }

    private sealed class WorkspaceApplyState(Server server, IReadOnlyList<WorkspaceAction> actions)
    {
        internal Server Server { get; set; } = server;
        internal Dictionary<string, string> BootstrapSources { get; } = actions
            .Where(action => action.Kind == WorkspaceActionKind.CaptureBootstrap)
            .ToDictionary(action => action.Target, action => action.SourceTarget!, StringComparer.Ordinal);
        internal Dictionary<string, SessionCreationResult> SessionCreations { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, WindowCreationResult> WindowCreations { get; } = new(StringComparer.Ordinal);
        internal Dictionary<WindowId, HashSet<PaneId>> KnownPanes { get; } = [];
        internal Dictionary<string, object> Targets { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, TmuxWaitChannel> Channels { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Owned { get; } = new(StringComparer.Ordinal);
        internal List<string> WindowTargets { get; } = [];
        internal List<string> Unsupported { get; } = [];

        internal T Get<T>(string target) where T : class => (T)Targets[target];

        internal WorkspaceResult? Result(IReadOnlyList<WorkspaceActionOutcome> journal, IReadOnlyList<WorkspaceActionOutcome> cleanup) =>
            Targets.TryGetValue("session", out object? session)
                ? new((Session)session, [.. WindowTargets.Select(Get<Window>)], Unsupported)
                {
                    Journal = journal,
                    CompensationJournal = cleanup,
                }
                : null;
    }
}
