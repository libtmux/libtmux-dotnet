namespace LibTmux.Workspace;

public sealed partial class WorkspaceBuilder
{
    /// <summary>Checks declaration and policy constraints without reading tmux or executing scripts.</summary>
    /// <param name="workspace">The immutable declaration to check.</param>
    /// <param name="options">The policies that will be used for planning.</param>
    /// <remarks>
    /// Reuse defers host-script admission until planning determines whether creation is needed.
    /// Endpoint identity, conflicts, paths on disk, shell syntax and tmux option support are
    /// not checked by local validation.
    /// </remarks>
    /// <exception cref="WorkspaceFormatException">The declaration cannot be applied under the supplied policies.</exception>
    /// <exception cref="ArgumentException">A declaration argument or policy value is invalid.</exception>
    public static void Validate(WorkspaceFile workspace, WorkspacePlanOptions? options = null) =>
        _ = PrepareWorkspace(workspace, options ?? new());

    /// <summary>Observes the endpoint and constructs an immutable workspace action plan.</summary>
    /// <param name="workspace">The immutable declaration, resolved explicitly when file-relative paths are needed.</param>
    /// <param name="options">The conflict, readiness and cleanup policies.</param>
    /// <param name="cancellationToken">Cancels read-only endpoint observation.</param>
    /// <returns>The actions and observed identity preconditions; no workspace actions are executed.</returns>
    /// <exception cref="WorkspaceFormatException">The declaration cannot be applied.</exception>
    /// <exception cref="TmuxSessionExistsException">The default policy refuses an existing session.</exception>
    /// <exception cref="ArgumentException">The endpoint has an opaque initializer or the options are invalid.</exception>
    public async Task<WorkspacePlan> PlanAsync(WorkspaceFile workspace, WorkspacePlanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        WorkspacePlanOptions policy = options ?? new();
        if (_server.ConnectionOptions.InitializeAsync is not null)
        {
            throw new ArgumentException("Workspace planning requires an endpoint without an opaque InitializeAsync callback.", nameof(workspace));
        }
        (NewSessionRequest sessionRequest, WorkspaceHostCommand? host) = PrepareWorkspace(workspace, policy);
        string sessionName = workspace.SessionName!;
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Server? observed = await _server.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (observed is null && policy.ServerStartup == WorkspaceServerStartup.RequireExisting)
            throw new LibTmuxException("The workspace plan requires an existing daemon.", TmuxDispatchState.NotDispatched);
        Session? existing = observed is null ? null : (await observed.GetSessionsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(session => string.Equals(session.Name, sessionName, StringComparison.Ordinal));
        DateTimeOffset completed = DateTimeOffset.UtcNow;
        if (existing is not null && policy.ExistingSession == WorkspaceExistingSession.Error)
        {
            throw new TmuxSessionExistsException($"Session '{sessionName}' already exists.", sessionName);
        }

        List<WorkspaceAction> actions = [];
        List<WorkspaceAction> compensation = [];
        if (existing is not null && policy.ExistingSession == WorkspaceExistingSession.Reuse)
        {
            actions.Add(new(WorkspaceActionKind.ReuseSession, "session"));
        }
        else
        {
            host ??= PlanHost(workspace, policy);
            if (host is not null)
                actions.Add(new WorkspaceAction<WorkspaceHostCommand>(WorkspaceActionKind.RunHostScript, "host", host));
            bool append = existing is not null && policy.ExistingSession == WorkspaceExistingSession.Append;
            bool replace = existing is not null && policy.ExistingSession == WorkspaceExistingSession.Replace;
            if (replace)
            {
                actions.Add(new WorkspaceAction<NewSessionRequest>(WorkspaceActionKind.CreateSession, "keepalive", new()
                {
                    Name = $"libtmux-workspace-{Guid.NewGuid():N}",
                    Command = "exec /bin/cat",
                    ExpectedGeneration = observed!.Generation,
                }));
                actions.Add(new(WorkspaceActionKind.CaptureBootstrap, "keepalive-bootstrap", "keepalive"));
                actions.Add(new(WorkspaceActionKind.RemoveSession, "existing"));
                compensation.Add(new(WorkspaceActionKind.UnlinkWindow, "keepalive-bootstrap"));
            }
            if (!append)
            {
                actions.Add(new WorkspaceAction<NewSessionRequest>(WorkspaceActionKind.CreateSession, "session",
                    sessionRequest with { ExpectedGeneration = observed?.Generation }));
                actions.Add(new(WorkspaceActionKind.CaptureBootstrap, "bootstrap", "session"));
                foreach ((string name, string value) in workspace.Options)
                {
                    actions.Add(new WorkspaceAction<SetOptionRequest>(WorkspaceActionKind.SetOption, "session", new(name, value)));
                }
                if (policy.CompensateOnFailure)
                    compensation.Insert(0, new(WorkspaceActionKind.UnlinkWindow, "bootstrap"));
            }

            for (int index = 0; index < workspace.Windows.Count; index++)
            {
                PlanWindow(workspace, workspace.Windows[index], index, policy, actions, compensation);
                if (index == 0 && !append)
                {
                    actions.Add(new(WorkspaceActionKind.UnlinkWindow, "bootstrap"));
                    actions.Add(new(WorkspaceActionKind.MoveToBaseIndex, "window:0", "session"));
                }
            }
            for (int index = workspace.Windows.Count - 1; index >= 0; index--)
            {
                if (!workspace.Windows[index].Focus)
                    continue;
                actions.Add(new(WorkspaceActionKind.SelectWindow, $"window:{index}"));
                break;
            }
            if (replace)
                actions.Add(new(WorkspaceActionKind.UnlinkWindow, "keepalive-bootstrap"));
            actions.Add(new(WorkspaceActionKind.CaptureResult, "session"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new WorkspacePlan(_server, sessionName, observed, existing, policy, started, completed, actions,
            [.. compensation.OrderBy(action => action.Kind == WorkspaceActionKind.CloseReadinessChannel ? 0 : 1)]);
    }

    private static (NewSessionRequest Request, WorkspaceHostCommand? Host) PrepareWorkspace(
        WorkspaceFile workspace, WorkspacePlanOptions policy)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        policy.Validate();
        if (string.IsNullOrWhiteSpace(workspace.SessionName) || workspace.Windows.Count == 0)
        {
            throw new WorkspaceFormatException("A workspace needs a session name and at least one window.");
        }

        NewSessionRequest sessionRequest = new()
        {
            Name = EscapeWorkspaceName(workspace.SessionName),
            WindowName = BootstrapWindowName,
            StartDirectory = StartDirectoryFor(workspace.Windows[0], workspace),
            Command = "exec /bin/cat",
            Environment = workspace.Environment,
        };
        _ = sessionRequest.ToCommand();
        ValidatePlanInput(workspace, policy);
        return (sessionRequest, policy.ExistingSession == WorkspaceExistingSession.Reuse
            ? null : PlanHost(workspace, policy));
    }

    private static void PlanWindow(WorkspaceFile workspace, WorkspaceWindow window, int windowIndex,
        WorkspacePlanOptions policy, List<WorkspaceAction> actions, List<WorkspaceAction> compensation)
    {
        string windowTarget = $"window:{windowIndex}";
        string? previousPane = null;
        for (int index = 0; index < Math.Max(1, window.Panes.Count); index++)
        {
            WorkspacePane pane = index < window.Panes.Count ? window.Panes[index] : new();
            string paneTarget = $"{windowTarget}/pane:{index}";
            if (index > 1)
                actions.Add(new WorkspaceAction<SelectLayoutRequest>(WorkspaceActionKind.ArrangePanes, windowTarget,
                    new() { Layout = "tiled" }));
            if (policy.Readiness == WorkspaceReadiness.Cooperative)
            {
                actions.Add(new(WorkspaceActionKind.OpenReadinessChannel, paneTarget, "session"));
                compensation.Insert(0, new(WorkspaceActionKind.CloseReadinessChannel, paneTarget));
            }
            if (index == 0)
            {
                actions.Add(new WorkspaceAction<NewWindowRequest>(WorkspaceActionKind.CreateWindow, windowTarget, new()
                {
                    Name = EscapeWorkspaceName(window.WindowName),
                    StartDirectory = StartDirectoryFor(window, workspace),
                    Environment = EnvironmentFor(workspace, window, pane),
                }, "session"));
                actions.Add(new(WorkspaceActionKind.CaptureFirstPane, paneTarget, windowTarget));
                if (policy.CompensateOnFailure)
                    compensation.Insert(0, new(WorkspaceActionKind.UnlinkWindow, windowTarget));
            }
            else
            {
                actions.Add(new WorkspaceAction<SplitPaneRequest>(WorkspaceActionKind.SplitPane, paneTarget, new()
                {
                    StartDirectory = DispatchDirectory(workspace, pane.StartDirectory ?? window.StartDirectory ?? workspace.StartDirectory),
                    Environment = EnvironmentFor(workspace, window, pane),
                }, previousPane));
            }
            if (policy.Readiness == WorkspaceReadiness.Cooperative)
            {
                actions.Add(new WorkspaceAction<TimeSpan>(WorkspaceActionKind.WaitForReadiness, paneTarget, policy.ReadinessTimeout));
                actions.Add(new(WorkspaceActionKind.CloseReadinessChannel, paneTarget));
            }
            foreach ((string name, string value) in pane.Options)
                actions.Add(new WorkspaceAction<SetOptionRequest>(WorkspaceActionKind.SetOption, paneTarget, new(name, value)));
            foreach (string command in workspace.ShellCommandsBefore.Concat(window.ShellCommandsBefore).Concat(pane.ShellCommandsBefore).Concat(pane.ShellCommands))
            {
                actions.Add(new WorkspaceAction<string>(WorkspaceActionKind.SendText, paneTarget, command));
            }
            previousPane = paneTarget;
        }
        if (!string.IsNullOrWhiteSpace(window.Layout))
            actions.Add(new WorkspaceAction<SelectLayoutRequest>(WorkspaceActionKind.SelectLayout, windowTarget, new() { Layout = window.Layout }));
        foreach ((string name, string value) in window.Options)
            actions.Add(new WorkspaceAction<SetOptionRequest>(WorkspaceActionKind.SetOption, windowTarget, new(name, value)));
        for (int index = window.Panes.Count - 1; index >= 0; index--)
        {
            if (!window.Panes[index].Focus)
                continue;
            actions.Add(new(WorkspaceActionKind.SelectPane, $"{windowTarget}/pane:{index}"));
            break;
        }
    }

    private static void ValidatePlanInput(WorkspaceFile workspace, WorkspacePlanOptions options)
    {
        ValidateEnvironment(workspace.Environment, options);
        ValidateText(workspace.SessionName);
        ValidateText(workspace.StartDirectory);
        ValidateOptions(workspace.Options);
        foreach (string command in workspace.ShellCommandsBefore)
            ValidateText(command);
        foreach (WorkspaceWindow window in workspace.Windows)
        {
            ValidateText(window.WindowName);
            ValidateText(window.StartDirectory);
            ValidateText(window.Layout);
            ValidateOptions(window.Options);
            ValidateEnvironment(window.Environment, options);
            foreach (string command in window.ShellCommandsBefore)
                ValidateText(command);
            foreach (WorkspacePane pane in window.Panes)
            {
                ValidateText(pane.StartDirectory);
                ValidateOptions(pane.Options);
                ValidateEnvironment(pane.Environment, options);
                foreach (string command in pane.ShellCommandsBefore.Concat(pane.ShellCommands))
                    ValidateText(command);
            }
        }
    }

    private static WorkspaceHostCommand? PlanHost(WorkspaceFile workspace, WorkspacePlanOptions policy)
    {
        if (workspace.BeforeScript is null)
            return null;
        if (!policy.AllowHostScripts)
            throw new WorkspaceFormatException("before_script requires AllowHostScripts in the workspace plan options.");
        if (workspace.DocumentDirectory is null)
            throw new WorkspaceFormatException("before_script requires an explicit document directory; call WorkspaceFile.Resolve before planning.");
        return new(workspace.BeforeScript, workspace.DocumentDirectory, policy.MaxHostOutputBytes,
            policy.HostScriptTimeout, workspace.Environment);
    }

    private static void ValidateOptions(IReadOnlyDictionary<string, string> options)
    {
        foreach ((string name, string value) in options)
        {
            _ = new SetOptionRequest(name, value);
            ValidateText(name);
            ValidateText(value);
        }
    }

    private static void ValidateEnvironment(IReadOnlyDictionary<string, string> environment, WorkspacePlanOptions options)
    {
        if (options.Readiness == WorkspaceReadiness.Cooperative && environment.ContainsKey("LIBTMUX_WORKSPACE_READY"))
            throw new WorkspaceFormatException("LIBTMUX_WORKSPACE_READY is reserved for the owned cooperative startup channel.");
    }

    private static void ValidateText(string? value)
    {
        if (value?.Contains('\0') == true)
            throw new WorkspaceFormatException("Workspace command arguments cannot contain NUL.");
    }

    // An empty conditional separates the escaped hash from '['; tmux otherwise
    // preserves '##[' as style syntax instead of producing one literal hash.
    private static string? EscapeWorkspaceName(string? name) =>
        name?.Replace("#", "###{?1,,}", StringComparison.Ordinal);
}
