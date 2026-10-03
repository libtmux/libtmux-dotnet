namespace LibTmux.Workspace;

public sealed partial class WorkspaceBuilder
{
    /// <summary>Checks declaration and policy constraints without reading tmux or executing scripts.</summary>
    /// <param name="workspace">The immutable declaration to check.</param>
    /// <param name="options">The policies that will be used for planning.</param>
    /// <remarks>
    /// Reuse defers host-script admission until planning determines whether creation is needed.
    /// Layout syntax, checksums and minimum cell counts are checked locally; version-specific
    /// layout names are checked during planning. Endpoint identity, conflicts, paths on disk,
    /// shell syntax and tmux option support are not checked by local validation.
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
            Server layoutServer = observed ?? _server;
            try
            {
                await layoutServer.ValidateLayoutsAsync(
                    workspace.Windows.Where(window => window.Layout is not null)
                        .Select(window => (window.Layout!, Math.Max(1, window.Panes.Count))),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException error)
            {
                for (int index = 0; index < workspace.Windows.Count; index++)
                {
                    WorkspaceWindow window = workspace.Windows[index];
                    if (window.Layout is null)
                        continue;
                    try
                    {
                        await layoutServer.ValidateLayoutsAsync(
                            [(window.Layout, Math.Max(1, window.Panes.Count))], cancellationToken).ConfigureAwait(false);
                    }
                    catch (ArgumentException specific)
                    {
                        string path = $"windows[{index}].layout";
                        throw workspace.At(path, $"Workspace path '{path}': {specific.Message}", specific);
                    }
                }
                throw new WorkspaceFormatException(error.Message, error);
            }

            host ??= PlanHost(workspace, policy);
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
                if (policy.CompensateOnFailure)
                    compensation.Insert(0, new(WorkspaceActionKind.UnlinkWindow, "bootstrap"));
            }
            if (host is not null)
                actions.Add(new WorkspaceAction<WorkspaceHostCommand>(WorkspaceActionKind.RunHostScript, "host", host));
            if (!append)
            {
                foreach ((string name, string value) in workspace.Options)
                {
                    actions.Add(new WorkspaceAction<SetOptionRequest>(WorkspaceActionKind.SetOption, "session", new(name, value)));
                }
            }

            for (int index = 0; index < workspace.Windows.Count; index++)
            {
                PlanWindow(workspace, workspace.Windows[index], index, index == 0 && !append, policy, actions, compensation);
                if (index == 0 && !append)
                {
                    actions.Add(new(WorkspaceActionKind.UnlinkWindow, "bootstrap"));
                    if (workspace.Windows[0].WindowIndex is int requestedIndex)
                        actions.Add(new WorkspaceAction<int>(WorkspaceActionKind.MoveToWindowIndex, "window:0", requestedIndex,
                            "session"));
                    else
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
        return new WorkspacePlan(_server, sessionName, observed, existing, policy, started, DateTimeOffset.UtcNow, actions,
            [.. compensation.OrderBy(action => action.Kind == WorkspaceActionKind.CloseReadinessChannel ? 0 : 1)]);
    }

    private static (NewSessionRequest Request, WorkspaceHostCommand? Host) PrepareWorkspace(
        WorkspaceFile workspace, WorkspacePlanOptions policy)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        policy.Validate();
        if (string.IsNullOrWhiteSpace(workspace.SessionName))
            throw workspace.At("session_name", "Workspace path 'session_name' needs a nonblank session name.");
        if (workspace.Windows.Count == 0)
            throw workspace.At("windows", "Workspace path 'windows' needs at least one window.");

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

    private static void PlanWindow(WorkspaceFile workspace, WorkspaceWindow window, int windowIndex, bool bootstrapWindow,
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
                    Index = bootstrapWindow ? null : window.WindowIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
            bool enter = pane.Enter ?? true;
            foreach (WorkspaceCommand command in workspace.BeforeCommands
                .Concat(window.BeforeCommands)
                .Concat(pane.BeforeCommands)
                .Concat(pane.Commands))
            {
                enter = command.Enter ?? enter;
                actions.Add(new WorkspaceAction<SendKeysRequest>(WorkspaceActionKind.SendText, paneTarget,
                    new SendKeysRequest { Text = command.Text, Enter = enter, Literal = true }));
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
        ValidateEnvironment(workspace, workspace.Environment, options, "environment");
        ValidateText(workspace, workspace.SessionName, "session_name");
        ValidateText(workspace, workspace.StartDirectory, "start_directory");
        ValidateOptions(workspace, workspace.Options, "options");
        ValidateCommands(workspace, workspace.BeforeCommands, "shell_command_before");
        HashSet<int> requestedIndexes = [];
        for (int windowIndex = 0; windowIndex < workspace.Windows.Count; windowIndex++)
        {
            WorkspaceWindow window = workspace.Windows[windowIndex];
            string windowPath = $"windows[{windowIndex}]";
            if (window.WindowIndex is int index && !requestedIndexes.Add(index))
            {
                string path = $"{windowPath}.window_index";
                throw workspace.At(path, $"Workspace path '{path}' declares window_index {index} more than once.");
            }
            ValidateText(workspace, window.WindowName, $"{windowPath}.window_name");
            ValidateText(workspace, window.StartDirectory, $"{windowPath}.start_directory");
            ValidateText(workspace, window.Layout, $"{windowPath}.layout");
            if (window.Layout is not null && !Server.IsValidLayoutCandidate(window.Layout, Math.Max(1, window.Panes.Count)))
            {
                string path = $"{windowPath}.layout";
                throw workspace.At(path,
                    $"Workspace path '{path}': Layout '{window.Layout}' is unknown, ambiguous, malformed, or has fewer cells than panes.");
            }
            ValidateOptions(workspace, window.Options, $"{windowPath}.options");
            ValidateEnvironment(workspace, window.Environment, options, $"{windowPath}.environment");
            ValidateCommands(workspace, window.BeforeCommands, $"{windowPath}.shell_command_before");
            for (int paneIndex = 0; paneIndex < window.Panes.Count; paneIndex++)
            {
                WorkspacePane pane = window.Panes[paneIndex];
                string panePath = $"{windowPath}.panes[{paneIndex}]";
                ValidateText(workspace, pane.StartDirectory, $"{panePath}.start_directory");
                ValidateOptions(workspace, pane.Options, $"{panePath}.options");
                ValidateEnvironment(workspace, pane.Environment, options, $"{panePath}.environment");
                ValidateCommands(workspace, pane.BeforeCommands, $"{panePath}.shell_command_before");
                ValidateCommands(workspace, pane.Commands, $"{panePath}.shell_command");
            }
        }
    }

    private static WorkspaceHostCommand? PlanHost(WorkspaceFile workspace, WorkspacePlanOptions policy)
    {
        if (workspace.BeforeScript is null)
            return null;
        if (!policy.AllowHostScripts)
            throw workspace.At("before_script", "before_script requires AllowHostScripts in the workspace plan options.");
        if (workspace.DocumentDirectory is null)
            throw workspace.At("before_script", "before_script requires an explicit document directory; call WorkspaceFile.Resolve before planning.");
        return new(workspace.BeforeScript, workspace.StartDirectory ?? workspace.DocumentDirectory, policy.MaxHostOutputBytes,
            policy.HostScriptTimeout, workspace.Environment);
    }

    private static void ValidateOptions(WorkspaceFile workspace, IReadOnlyDictionary<string, string> options, string parentPath)
    {
        foreach ((string name, string value) in options)
        {
            string path = $"{parentPath}.{name}";
            try
            {
                _ = new SetOptionRequest(name, value);
            }
            catch (ArgumentException failure)
            {
                throw workspace.At(path, $"Workspace path '{path}' has an invalid option: {failure.Message}", failure);
            }
            ValidateText(workspace, name, path);
            ValidateText(workspace, value, path);
        }
    }

    private static void ValidateEnvironment(WorkspaceFile workspace, IReadOnlyDictionary<string, string> environment,
        WorkspacePlanOptions options, string parentPath)
    {
        if (options.Readiness == WorkspaceReadiness.Cooperative && environment.ContainsKey("LIBTMUX_WORKSPACE_READY"))
        {
            string path = $"{parentPath}.LIBTMUX_WORKSPACE_READY";
            throw workspace.At(path, $"Workspace path '{path}': LIBTMUX_WORKSPACE_READY is reserved for the owned cooperative startup channel.");
        }
    }

    private static void ValidateCommands(WorkspaceFile workspace, IReadOnlyList<WorkspaceCommand> commands, string parentPath)
    {
        for (int index = 0; index < commands.Count; index++)
        {
            string path = $"{parentPath}[{index}]";
            if (workspace.SourceLocations.ContainsKey($"{path}.cmd"))
                path += ".cmd";
            ValidateText(workspace, commands[index].Text, path);
        }
    }

    private static void ValidateText(WorkspaceFile workspace, string? value, string path)
    {
        if (value?.Contains('\0') == true)
            throw workspace.At(path, $"Workspace path '{path}' cannot contain NUL.");
    }

    // An empty conditional separates the escaped hash from '['; tmux otherwise
    // preserves '##[' as style syntax instead of producing one literal hash.
    private static string? EscapeWorkspaceName(string? name) =>
        name?.Replace("#", "###{?1,,}", StringComparison.Ordinal);
}
