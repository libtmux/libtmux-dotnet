using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using LibTmux.Internal;

namespace LibTmux.Workspace.Cli;

[UnsupportedOSPlatform("windows")]
internal sealed class ExecutionCommands(CliContext context, Invocation invocation, Output output)
{
    private static readonly string[] InterpreterSuffixes = ["python", "ruby", "node"];
    // macOS's /bin/sh is bash, so pane_current_command can differ from
    // basename(default-shell) for an ordinary shell.
    private static readonly string[] OrdinaryShellNames = ["sh", "bash", "zsh", "dash", "ash", "ksh", "mksh", "fish", "csh", "tcsh"];
    private readonly DocumentStore _documents = new(context);
    private Server? _server;
    private ServerGeneration? _loadGeneration;
    private Server Server => _server ??= Server.Open(Connection());

    internal ServerConnectionOptions Connection()
    {
        string? socket = invocation.Text("socket_path");
        string? name = invocation.Text("socket_name");
        if (socket is null && name is null) socket = LoadHandoff.CurrentSocket(context, out _);
        if (socket is not null) socket = Path.GetFullPath(socket, context.Directory);
        return new ServerConnectionOptions(tmuxBinaryPath: TmuxExecutable(), socketName: name, socketPath: socket, configurationFile: invocation.Text("tmux_config"), colorMode: invocation.Flag("colors256") ? TmuxColorMode.Colors256 : TmuxColorMode.Default, childEnvironment: context.Environment);
    }

    // A missing tmux executable is its own tmux_unavailable code, not the
    // generic executable_unavailable shared with EDITOR/before_script.
    private string TmuxExecutable()
    {
        try
        {
            return context.Executable(context.Environment.GetValueOrDefault("LIBTMUX_TMUX") ?? "tmux");
        }
        catch (CliException failure) when (failure.Code == "executable_unavailable")
        {
            throw new CliException("tmux_unavailable", failure.Message);
        }
    }

    internal async Task<int> LoadAsync()
    {
        if (invocation.Flag("colors88")) throw new CliException("unsupported_color_mode", "88-color mode is unsupported on tmux 3.2a and newer. Use -2 for 256 colors.", 2);
        if (invocation.Machine && !invocation.Flag("detached") && !invocation.Flag("append")) throw new CliException("usage", "Machine load requires -d or --append.", 2);
        output.PrepareProgress();
        string[] files = invocation.Many("files");
        var inputs = files.Select((file, index) =>
        {
            string path = _documents.Resolve(file);
            JsonObject document = DocumentStore.Read(path);
            return (Path: path, Document: document, Plan: WorkspacePlan.Parse(document, path, _documents, index == files.Length - 1 ? invocation.Text("session_name") : null));
        }).ToArray();
        bool extensions = inputs.Any(input => input.Document["plugins"] is not null and not JsonArray { Count: 0 } || input.Document["workspace_builder"] is not null);
        LoadHandoff handoff = new(context, invocation, output);
        await handoff.ResolveAsync(Connection(), inputs[^1].Plan.Name).ConfigureAwait(false);
        if (extensions && handoff.Mode == LoadMode.Append) throw new CliException("unsupported_append_extensions", "Append with Python workspace extensions is unsupported. Load the extensions into a separate session with -d.", 2);
        if (extensions && handoff.Mode is LoadMode.Attach or LoadMode.Switch) throw new CliException("unsupported_attached_extensions", "Python extension handoff is not yet supported. Load the extensions with -d.", 2);
        _server = handoff.Server;
        _loadGeneration = _server?.Generation;
        try
        {
            await Server.ValidateLayoutsAsync(
                    inputs.SelectMany(static input => input.Plan.Windows)
                        .Where(static window => window.Layout is not null)
                        .Select(static window => (window.Layout!, window.Panes.Length)),
                    context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException error)
        {
            throw new CliException("invalid_workspace", error.Message);
        }
        (Session Session, string Name)? appendTarget = handoff.Mode == LoadMode.Append ? (handoff.CurrentSession!, handoff.CurrentSessionName!) : null;
        if (extensions)
            return await new ProcessCommands(context, invocation, output).BridgeLoadAsync(handoff.Mode == LoadMode.Detached).ConfigureAwait(false);
        Session? finalSession = null;
        (string? columns, string? rows) = SessionSize();
        JsonArray results = [];
        JsonArray errors = [];
        await output.EventAsync("started", new { inputs = inputs.Length }).ConfigureAwait(false);
        for (int index = 0; index < inputs.Length; index++)
        {
            string stage = "workspace-started";
            var input = inputs[index];
            string? session = null;
            Session? ownedSession = null;
            string sessionName = input.Plan.Name;
            string? completedStage = null;
            bool created = false;
            bool changed = false;
            // Declared here, not in the try, so the catch block can also kill
            // it: scaffolding, never part of the user's document.
            string? bootstrap = null;
            // True once the document's own first window exists. Bootstrap is
            // the only window before that; killing it would kill the session.
            bool windowCreated = false;
            // Windows added to a session this load did not create. They stay
            // behind on failure, so the report has to name them.
            List<string> appended = [];
            async Task<string> Change(IReadOnlyList<string> arguments)
            {
                string result = await Command(arguments).ConfigureAwait(false);
                changed = true;
                return result;
            }
            // Resolved once per session: default-shell is static, unlike a
            // live pane_current_command read, which races the shell starting.
            bool readinessResolved = false;
            string? readinessShell = null;
            async Task<string?> ReadinessShellAsync()
            {
                if (readinessResolved) return readinessShell;
                readinessResolved = true;
                if (input.Plan.Readiness == "never") return null;
                if ((await Field(session!, "default-command").ConfigureAwait(false)).Length > 0) return null;
                string shellName = Path.GetFileName(await Field(session!, "default-shell").ConfigureAwait(false));
                return readinessShell = input.Plan.Readiness == "always" || shellName == "zsh" ? shellName : null;
            }
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await output.EventAsync(stage = "workspace-started", new { input_index = index, input = input.Path }).ConfigureAwait(false);
                if (appendTarget is { } retained)
                {
                    session = retained.Session.Id.ToString();
                    sessionName = retained.Name;
                    await Command(["has-session", "-t", session]).ConfigureAwait(false);
                }
                else
                {
                    TmuxCommandResult exists = await Server.ExecuteCommandAsync(["has-session", "-t", "=" + input.Plan.Name], context.CancellationToken).ConfigureAwait(false);
                    if (exists.ExitCode == 0)
                    {
                        string[] identity = (await Command(["display-message", "-p", "-t", "=" + input.Plan.Name + ":", "#{session_id}\t#{pid}:#{start_time}"]).ConfigureAwait(false)).TrimEnd('\n').Split('\t');
                        session = identity[0];
                        // A name that exists is not evidence the workspace
                        // behind it does. Reuse is reported only once every
                        // window the document declares is present.
                        string[] absent = MissingWindows(input.Plan.Windows, await Command(["list-windows", "-t", session, "-F", "#{window_index}\t#{window_name}"]).ConfigureAwait(false));
                        if (absent.Length > 0)
                            throw new CliException("destination_exists", $"Session '{input.Plan.Name}' is already running without {string.Join(", ", absent)}. Remove it and load again, or load under another name.");
                        finalSession = await BindSessionAsync(session, TmuxConnection.ParseGeneration(identity[1])).ConfigureAwait(false);
                        results.Add(Result(index, input.Path, session, input.Plan.Name, "reused"));
                        await output.EventAsync("workspace-completed", new { input_index = index, session_id = session, status = "reused" }).ConfigureAwait(false);
                        if (!invocation.Machine) output.Human("Using existing session " + input.Plan.Name, "success");
                        continue;
                    }
                }
                await output.ProgressAsync(progress => progress.StartWorkspace(input.Plan, sessionName), force: true).ConfigureAwait(false);
                if (session is null)
                {
                    stage = "session-created";
                    List<string> start = ["new-session", "-d", "-s", input.Plan.Name, "-c", input.Plan.Directory];
                    if (columns is not null) start.AddRange(["-x", columns]);
                    if (rows is not null) start.AddRange(["-y", rows]);
                    start.AddRange(["-P", "-F", "#{session_id}\t#{window_id}\t#{pid}:#{start_time}"]);
                    foreach (var variable in input.Plan.Environment) start.AddRange(["-e", variable.Key + "=" + variable.Value]);
                    string[] identifiers = (await Change(start).ConfigureAwait(false)).TrimEnd('\n').Split('\t');
                    session = identifiers[0];
                    bootstrap = identifiers[1];
                    created = true;
                    finalSession = ownedSession = await BindSessionAsync(session, TmuxConnection.ParseGeneration(identifiers[2])).ConfigureAwait(false);
                    int temporaryIndex = 99999;
                    while (input.Plan.Windows.Any(window => window.Index == temporaryIndex)) temporaryIndex++;
                    await Change(["move-window", "-s", bootstrap, "-t", session + ":" + temporaryIndex.ToString(CultureInfo.InvariantCulture)]).ConfigureAwait(false);
                    await output.EventAsync(stage, new { input_index = index, session_id = session, session_name = input.Plan.Name }).ConfigureAwait(false);
                    completedStage = stage;
                }
                if (input.Plan.BeforeScript is string script)
                {
                    stage = "before-script";
                    string[] scriptArguments = ProcessCommands.SplitArguments(script);
                    if (scriptArguments.Length == 0) throw new CliException("invalid_workspace", "before_script must name an executable.");
                    if (scriptArguments[0].StartsWith('.')) scriptArguments[0] = Path.GetFullPath(scriptArguments[0], Path.GetDirectoryName(input.Path)!);
                    await output.EventAsync("script-started", new { input_index = index }).ConfigureAwait(false);
                    ChildResult child;
                    try
                    {
                        child = await ProcessCommands.RunProcessAsync(context, output, scriptArguments[0], scriptArguments[1..], input.Plan.Directory, stream: true, inputIndex: index).ConfigureAwait(false);
                    }
                    catch (CliException failure) when (failure.Code == "executable_unavailable")
                    {
                        // Never ran, so there is nothing to report beyond the
                        // zero-value child a start failure leaves behind.
                        await output.EventAsync("script-completed", new { input_index = index, child_status = 0, truncated = false }).ConfigureAwait(false);
                        // A before_script that cannot start is a before_script
                        // failure like a nonzero exit, not a missing tmux.
                        throw new CliException("script_failed", $"before_script could not start: {failure.Message}");
                    }
                    await output.EventAsync("script-completed", new { input_index = index, child_status = child.ExitCode, truncated = child.Truncated }).ConfigureAwait(false);
                    if (child.ExitCode != 0) throw new CliException("script_failed", $"before_script exited with status {child.ExitCode}.");
                    completedStage = stage;
                }
                stage = "session-options";
                foreach (var option in input.Plan.GlobalOptions) await Change(["set-option", "-g", option.Key, OptionValue(option.Value)]).ConfigureAwait(false);
                foreach (var option in input.Plan.Options) await Change(["set-option", "-t", session, option.Key, OptionValue(option.Value)]).ConfigureAwait(false);
                foreach (var variable in input.Plan.Environment) await Change(["set-environment", "-t", session, variable.Key, variable.Value]).ConfigureAwait(false);
                completedStage = stage;
                string? focusedWindow = null;
                string? firstWindow = null;
                int windowOrdinal = 0;
                foreach (WindowPlan window in input.Plan.Windows)
                {
                    windowOrdinal++;
                    await output.ProgressAsync(progress => progress.StartWindow(window, windowOrdinal)).ConfigureAwait(false);
                    stage = "window-created";
                    PanePlan first = window.Panes[0];
                    List<string> args = ["new-window", "-d", "-P", "-F", "#{window_id}\t#{pane_id}", "-t", session + ":" + window.Index?.ToString(CultureInfo.InvariantCulture)];
                    if (window.Name is not null) args.AddRange(["-n", window.Name]);
                    PaneArguments(args, first);
                    string[] identifiers = (await Change(args).ConfigureAwait(false)).TrimEnd('\n').Split('\t');
                    windowCreated = true;
                    string windowId = identifiers[0];
                    string paneId = identifiers[1];
                    if (appendTarget is not null) appended.Add(window.Name ?? windowId);
                    firstWindow ??= windowId;
                    if (window.Focus) focusedWindow = windowId;
                    foreach (var option in window.Options) await Change(["set-window-option", "-t", windowId, option.Key, OptionValue(option.Value)]).ConfigureAwait(false);
                    await output.EventAsync(stage, new { input_index = index, session_id = session, window_id = windowId, window_index = windowOrdinal, window_name = window.Name }).ConfigureAwait(false);
                    completedStage = stage;
                    string? focusedPane = null;
                    // Every pane exists and the layout is final before any
                    // shell is typed into: a pane resized after its command
                    // redraws the prompt at a stale width and leaves the
                    // shell's partial-line marker behind.
                    string[] paneIds = new string[window.Panes.Length];
                    paneIds[0] = paneId;
                    for (int paneIndex = 1; paneIndex < window.Panes.Length; paneIndex++)
                    {
                        List<string> split = ["split-window", "-d", "-P", "-F", "#{pane_id}", "-t", paneIds[paneIndex - 1]];
                        PaneArguments(split, window.Panes[paneIndex]);
                        paneIds[paneIndex] = (await Change(split).ConfigureAwait(false)).TrimEnd('\n');
                        await Change(["select-layout", "-t", windowId, "tiled"]).ConfigureAwait(false);
                    }
                    if (window.Layout is not null) await Change(["select-layout", "-t", windowId, window.Layout]).ConfigureAwait(false);
                    for (int paneIndex = 0; paneIndex < window.Panes.Length; paneIndex++)
                    {
                        PanePlan pane = window.Panes[paneIndex];
                        paneId = paneIds[paneIndex];
                        await output.ProgressAsync(progress => progress.StartPane(paneIndex + 1)).ConfigureAwait(false);
                        await output.EventAsync(stage = "pane-created", new { input_index = index, session_id = session, window_id = windowId, window_index = windowOrdinal, pane_id = paneId, pane_index = paneIndex + 1 }).ConfigureAwait(false);
                        completedStage = stage;
                        if (pane.Focus) focusedPane = paneId;
                        if (pane.Commands.Length > 0 && pane.Shell is null && await ReadinessShellAsync().ConfigureAwait(false) is string expectedShell)
                        {
                            long started = System.Diagnostics.Stopwatch.GetTimestamp();
                            while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
                            {
                                string[] sample = (await Command(["display-message", "-p", "-t", paneId, "#{pane_current_command}\t#{cursor_x},#{cursor_y}"]).ConfigureAwait(false)).TrimEnd('\n').Split('\t');
                                if (sample.Length == 2 && sample[0] == expectedShell && sample[1] != "0,0") break;
                                await Task.Delay(50, context.CancellationToken).ConfigureAwait(false);
                            }
                        }
                        foreach (CommandPlan command in pane.Commands)
                        {
                            stage = "pane-command";
                            await Task.Delay(TimeSpan.FromSeconds(command.Before), context.CancellationToken).ConfigureAwait(false);
                            await Change(["send-keys", "-t", paneId, "-l", "--", command.Text]).ConfigureAwait(false);
                            if (command.Enter) await Change(["send-keys", "-t", paneId, "Enter"]).ConfigureAwait(false);
                            await Task.Delay(TimeSpan.FromSeconds(command.After), context.CancellationToken).ConfigureAwait(false);
                            completedStage = stage;
                        }
                        await output.ProgressAsync(progress => progress.CompletePane()).ConfigureAwait(false);
                        await output.EventAsync(stage = "pane-completed", new { input_index = index, session_id = session, window_id = windowId, window_index = windowOrdinal, pane_id = paneId, pane_index = paneIndex + 1 }).ConfigureAwait(false);
                        completedStage = stage;
                    }
                    stage = "window-finalized";
                    foreach (var option in window.OptionsAfter) await Change(["set-window-option", "-t", windowId, option.Key, OptionValue(option.Value)]).ConfigureAwait(false);
                    if (focusedPane is not null) await Change(["select-pane", "-t", focusedPane]).ConfigureAwait(false);
                    completedStage = stage;
                    await output.ProgressAsync(progress => progress.CompleteWindow(), force: true).ConfigureAwait(false);
                    await output.EventAsync(stage = "window-completed", new { input_index = index, session_id = session, window_id = windowId, window_index = windowOrdinal }).ConfigureAwait(false);
                    completedStage = stage;
                }
                stage = "workspace-finalized";
                if (bootstrap is not null) await Change(["kill-window", "-t", bootstrap]).ConfigureAwait(false);
                // Appending must not move the client off its current window
                // unless an appended window sets focus: true -- firstWindow
                // is only the fallback for a session load builds fresh, never
                // for one the user already owns.
                string? active = appendTarget is not null ? focusedWindow : focusedWindow ?? firstWindow;
                if (active is not null) await Change(["select-window", "-t", active]).ConfigureAwait(false);
                results.Add(Result(index, input.Path, session, sessionName, created ? "created" : "appended"));
                await output.EventAsync(stage = "workspace-completed", new { input_index = index, session_id = session }).ConfigureAwait(false);
                if (!invocation.Machine) { output.Human(created ? "Loaded " : "Appended ", "success", false); output.Human(sessionName, "subject"); }
            }
            catch (Exception failure) when (failure is CliException or OperationCanceledException or LibTmuxException or StaleServerGenerationException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                bool removed = false;
                string? cleanupError = null;
                // A session this load created is removed on every failure.
                // Leaving a half-built one behind is what lets the next run
                // find the name, call it reused, and report success.
                if (ownedSession is not null)
                {
                    using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(3));
                    try
                    {
                        TmuxCommandResult deletion = await ownedSession.ExecuteCommandAsync(["kill-session"], cancellationToken: cleanup.Token).ConfigureAwait(false);
                        removed = deletion.ExitCode == 0;
                        if (!removed) cleanupError = string.Join("\n", deletion.StandardErrorLines);
                    }
                    catch (Exception deletion) when (deletion is LibTmuxException or StaleServerGenerationException or OperationCanceledException or IOException or UnauthorizedAccessException)
                    {
                        cleanupError = deletion.Message;
                    }
                }
                // Bootstrap is scaffolding, not user content, so it still gets
                // cleaned up here. Best-effort and silent.
                if (!removed && bootstrap is not null && windowCreated)
                {
                    using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(3));
                    try { await Server.ExecuteCommandAsync(["kill-window", "-t", bootstrap], cleanup.Token).ConfigureAwait(false); }
                    catch (Exception deletion) when (deletion is LibTmuxException or StaleServerGenerationException or OperationCanceledException or IOException or UnauthorizedAccessException) { }
                }
                // A failed input still gets a results[] record: session_id is
                // null if load never got far enough to identify one.
                results.Add(new JsonObject
                {
                    ["input_index"] = index,
                    ["input"] = input.Path,
                    ["session_id"] = session,
                    ["session_name"] = sessionName,
                    ["reused"] = session is not null && !created,
                    ["status"] = "failed",
                });
                string code = failure is CliException cli ? cli.Code : failure is OperationCanceledException ? "interrupted" : failure is StaleServerGenerationException ? "stale_server" : failure is IOException or UnauthorizedAccessException ? "output_failed" : "tmux_failed";
                string message = appended.Count == 0 ? failure.Message : $"{failure.Message} Windows kept in {sessionName}: {string.Join(", ", appended)}.";
                errors.Add(new JsonObject { ["code"] = code, ["message"] = message, ["input_index"] = index, ["completed_stage"] = completedStage, ["failed_stage"] = stage, ["session_id"] = session, ["created"] = created, ["changed"] = changed, ["removed"] = removed });
                if (cleanupError is not null) errors[^1]!["cleanup_error"] = cleanupError;
                // A failed input's own results[] record (added above) must not
                // count toward "partial" -- only a genuinely completed input does.
                bool anySucceeded = results.Any(item => item!["status"]!.ToString() != "failed");
                // "error" means nothing this load made survives. A session it
                // found rather than created is still standing, so that is
                // "partial" whether or not this load changed it.
                bool retained = (session is not null && !created) || (changed && !removed);
                var summary = new { schema_version = 1, command = "load", status = anySucceeded || retained ? "partial" : "error", results, errors };
                using CancellationTokenSource reporting = new(TimeSpan.FromSeconds(3));
                try
                {
                    await output.EventAsync("failed", summary, reporting.Token).ConfigureAwait(false);
                    // Human mode never prints a machine record; the sentence
                    // DiagnosticAsync writes below is the whole human report.
                    if (invocation.Machine && !invocation.Flag("ndjson")) await output.ResultAsync(summary, cancellationToken: reporting.Token).ConfigureAwait(false);
                    await output.DiagnosticAsync(code, message).ConfigureAwait(false);
                }
                catch (Exception interrupted) when (interrupted is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    await output.DiagnosticAsync(code, message, summary).ConfigureAwait(false);
                }
                return failure is OperationCanceledException ? 130 : 1;
            }
        }
        var completed = new { schema_version = 1, command = "load", status = "ok", results, errors };
        try
        {
            await output.EventAsync("completed", completed).ConfigureAwait(false);
            if (invocation.Machine && !invocation.Flag("ndjson")) await output.ResultAsync(completed).ConfigureAwait(false);
            await output.HandoffAsync().ConfigureAwait(false);
            if (finalSession is not null) await handoff.CompleteAsync(finalSession).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or OperationCanceledException or CliException or LibTmuxException or StaleServerGenerationException)
        {
            string code = failure is CliException cli ? cli.Code : failure is OperationCanceledException ? "interrupted" : failure is StaleServerGenerationException ? "stale_server" : failure is IOException or UnauthorizedAccessException ? "output_failed" : "attach_failed";
            string recorded = "Recorded load results:\n" + string.Join("\n", results.Select(result => $"  {result!["session_name"]} ({result["session_id"]}, {result["status"]})"));
            await output.DiagnosticAsync(code, failure.Message, completed, recorded).ConfigureAwait(false);
            return failure is OperationCanceledException ? 130 : failure is CliException status ? status.ExitCode : 1;
        }
        return 0;
    }

    internal async Task FreezeAsync()
    {
        if (!invocation.Machine && invocation.Text("save_to") is null)
            throw new CliException("destination_required", "Capture needs a destination. Pass --save-to, or use --json or --ndjson for the document.", 2);
        string? supplied = invocation.Many("sessions").FirstOrDefault();
        string? target = supplied is null ? await CurrentPaneTarget().ConfigureAwait(false) : await NamedSessionTarget(supplied).ConfigureAwait(false);
        if (target is null)
        {
            string[] sessions = await LiveSessionsAsync().ConfigureAwait(false);
            if (sessions.Length == 0) throw new CliException("session_not_found", "No live sessions to capture.");
            target = sessions.Length == 1 ? sessions[0] : await NamedSessionTarget(new ReadCommands(context, invocation, output).Prompt("Session name: ")).ConfigureAwait(false);
        }
        string session = await Field(target, "session_id").ConfigureAwait(false);
        JsonObject document = new() { ["session_name"] = await Field(session, "session_name").ConfigureAwait(false) };
        // Omit shell_command for the session's own default shell.
        // A leading '-' marks a login-shell invocation of the same binary.
        string defaultShell = Path.GetFileName(await Field(session, "default-shell").ConfigureAwait(false));
        JsonArray windows = [];
        foreach (string window in (await Command(["list-windows", "-t", session, "-F", "#{window_id}"]).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonObject captured = new()
            {
                ["window_name"] = await Field(window, "window_name").ConfigureAwait(false),
                ["window_index"] = int.Parse(await Field(window, "window_index").ConfigureAwait(false), CultureInfo.InvariantCulture),
                ["layout"] = await Field(window, "window_layout").ConfigureAwait(false),
                ["focus"] = await Field(window, "window_active").ConfigureAwait(false) == "1",
            };
            JsonObject options = [];
            foreach (string row in (await Command(["show-options", "-w", "-t", window]).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string option = row.Split(' ', 2)[0];
                options[option] = (await Command(["show-options", "-w", "-t", window, "-v", option]).ConfigureAwait(false)).TrimEnd('\n');
            }
            // options_after matches load's own boundary for
            // automatic-rename: off, which only holds applied after panes exist.
            captured["options_after"] = options;
            JsonArray panes = [];
            foreach (string pane in (await Command(["list-panes", "-t", window, "-F", "#{pane_id}"]).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string currentCommand = await Field(pane, "pane_current_command").ConfigureAwait(false);
                string trimmedCommand = currentCommand.TrimStart('-');
                bool skipCommand = OrdinaryShellNames.Contains(trimmedCommand, StringComparer.Ordinal) || trimmedCommand == defaultShell || InterpreterSuffixes.Any(suffix => currentCommand.EndsWith(suffix, StringComparison.Ordinal));
                JsonObject capturedPane = new() { ["start_directory"] = await Field(pane, "pane_current_path").ConfigureAwait(false), ["focus"] = await Field(pane, "pane_active").ConfigureAwait(false) == "1" };
                if (!skipCommand) capturedPane["shell_command"] = new JsonArray(currentCommand);
                panes.Add(capturedPane);
            }
            captured["panes"] = panes;
            windows.Add(captured);
        }
        document["windows"] = windows;
        string format = invocation.Text("workspace_format") ?? "yaml";
        new ReadCommands(context, invocation, output).SaveOrPrint(document, format);
        if (!invocation.Flag("ndjson") && invocation.Text("save_to") is null) await output.WarningAsync("capture_lossy", "Capture preserves current commands and window options. Original command arguments, history, hooks and plugin state cannot be recovered.").ConfigureAwait(false);
    }

    private static JsonObject Result(int index, string path, string session, string name, string status) => new() { ["input_index"] = index, ["input"] = path, ["session_id"] = session, ["session_name"] = name, ["status"] = status, ["reused"] = status is "reused" or "appended", ["completed_stage"] = "workspace-completed" };

    // Each live window answers at most one declared window, so a document
    // that names two windows the same needs two of them present. An index
    // identifies a window on its own; a name only when no index was given.
    private static string[] MissingWindows(IReadOnlyList<WindowPlan> declared, string listing)
    {
        List<(int Index, string Name)> live = listing
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split('\t'))
            .Where(fields => fields.Length == 2 && int.TryParse(fields[0], CultureInfo.InvariantCulture, out _))
            .Select(fields => (Index: int.Parse(fields[0], CultureInfo.InvariantCulture), Name: fields[1]))
            .ToList();
        List<string> missing = [];
        foreach (WindowPlan window in declared)
        {
            int found = window.Index is int index
                ? live.FindIndex(candidate => candidate.Index == index)
                : window.Name is string name ? live.FindIndex(candidate => candidate.Name == name) : live.Count > 0 ? 0 : -1;
            if (found < 0) missing.Add(window.Name is string absent ? $"window '{absent}'" : window.Index is int position ? $"a window at index {position.ToString(CultureInfo.InvariantCulture)}" : "one of its windows");
            else live.RemoveAt(found);
        }
        return missing.ToArray();
    }

    private static void PaneArguments(List<string> arguments, PanePlan pane)
    {
        if (pane.Directory is not null) arguments.AddRange(["-c", pane.Directory]);
        foreach (var variable in pane.Environment) arguments.AddRange(["-e", variable.Key + "=" + variable.Value]);
        if (pane.Shell is not null) arguments.Add(pane.Shell);
    }

    private static string OptionValue(string value) => value switch { "true" => "on", "false" => "off", _ => value };

    // Mirrors tmuxp so an attached load sizes the session to the terminal it
    // was run from, instead of a fixed 80x24 that tmux stretches once a
    // client attaches -- which is what put every main-pane layout at the
    // wrong ratio.
    private (string? Columns, string? Rows) SessionSize()
    {
        string columns = Dimension("TMUXP_DEFAULT_COLUMNS", "COLUMNS", 80);
        string rows = Dimension("TMUXP_DEFAULT_ROWS", "ROWS", 24);
        if (context.Environment.TryGetValue("TMUXP_DETECT_TERMINAL_SIZE", out string? detect) && detect != "1") return (null, null);
        if (context.Terminal && ProgressDisplay.StandardOutputSize() is { } size)
        {
            columns = size.Width.ToString(CultureInfo.InvariantCulture);
            rows = size.Height.ToString(CultureInfo.InvariantCulture);
        }
        return (DimensionOverride("COLUMNS", columns), DimensionOverride("LINES", rows));
    }

    private string Dimension(string primary, string fallback, int value)
    {
        string? supplied = context.Environment.GetValueOrDefault(primary);
        string variable = supplied is null ? fallback : primary;
        string raw = supplied ?? context.Environment.GetValueOrDefault(fallback) ?? value.ToString(CultureInfo.InvariantCulture);
        if (!int.TryParse(raw, CultureInfo.InvariantCulture, out int parsed) || parsed is < 1 or > 65535) throw new CliException("invalid_dimension", variable + " must be an integer from 1 through 65535.", 2);
        return raw;
    }

    private string DimensionOverride(string name, string current)
    {
        string? raw = context.Environment.GetValueOrDefault(name);
        if (string.IsNullOrEmpty(raw)) return current;
        if (!int.TryParse(raw, CultureInfo.InvariantCulture, out int parsed) || parsed is < 1 or > 65535) throw new CliException("invalid_dimension", name + " must be an integer from 1 through 65535.", 2);
        return raw;
    }

    private async Task<string> Field(string target, string field) => (await Command(["display-message", "-p", "-t", target, "#{" + field + "}"]).ConfigureAwait(false)).TrimEnd('\n');

    private async Task<string?> CurrentPaneTarget()
    {
        if (LoadHandoff.CurrentSocket(context, out int processId) is null || !PaneId.TryParse(context.Environment.GetValueOrDefault("TMUX_PANE"), out PaneId pane)) return null;
        string running = (await Command(["display-message", "-p", "#{pid}"]).ConfigureAwait(false)).TrimEnd('\n');
        return running == processId.ToString(CultureInfo.InvariantCulture) ? pane.ToString() : null;
    }

    // A socket with no server behind it holds no session, which tmux reports
    // by refusing the connection rather than by answering with an empty list.
    private async Task<string[]> LiveSessionsAsync()
    {
        TmuxCommandResult listed = await Server.ExecuteCommandAsync(["list-sessions", "-F", "#{session_id}"], context.CancellationToken).ConfigureAwait(false);
        if (listed.ExitCode == 0)
            return System.Text.Encoding.UTF8.GetString(listed.StandardOutput.Span).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string stderr = string.Join("\n", listed.StandardErrorLines);
        if (stderr.StartsWith("no server running on ", StringComparison.Ordinal)
            || (stderr.StartsWith("error connecting to ", StringComparison.Ordinal)
                && (stderr.EndsWith(" (No such file or directory)", StringComparison.Ordinal)
                    || stderr.EndsWith(" (Connection refused)", StringComparison.Ordinal))))
        {
            return [];
        }
        throw new CliException("tmux_failed", stderr.Length == 0 ? $"tmux exited {listed.ExitCode}" : $"tmux exited {listed.ExitCode}: {stderr}");
    }

    private async Task<string> NamedSessionTarget(string name)
    {
        TmuxCommandResult exists = await Server.ExecuteCommandAsync(["has-session", "-t", "=" + name], context.CancellationToken).ConfigureAwait(false);
        if (exists.ExitCode != 0) throw new CliException("session_not_found", $"Session '{name}' was not found.");
        return "=" + name + ":";
    }

    private async Task<Session> BindSessionAsync(string id, ServerGeneration generation)
    {
        if (_loadGeneration is ServerGeneration expected && generation != expected)
            throw new StaleServerGenerationException("The load server changed before session selection.", expected, generation);
        _server = await Server.ConnectAsync(context.CancellationToken).ConfigureAwait(false);
        if (_server.Generation != generation)
            throw new StaleServerGenerationException("The load server changed after session selection.", generation, _server.Generation!.Value);
        _loadGeneration = generation;
        return await _server.GetSessionAsync(SessionId.Parse(id), context.CancellationToken).ConfigureAwait(false);
    }

    private async Task<string> Command(IReadOnlyList<string> arguments)
    {
        TmuxCommandResult result = _loadGeneration is not ServerGeneration generation
            ? await Server.ExecuteCommandAsync(arguments, context.CancellationToken).ConfigureAwait(false)
            : await Server.Chain().Then(new TmuxCommand(arguments[0], arguments.Skip(1).ToArray()) { RequiredGeneration = generation }).ExecuteAsync(context.CancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // No trailing "exited N: " separator when tmux wrote nothing.
            string stderr = string.Join("\n", result.StandardErrorLines);
            throw new CliException(
                "tmux_failed",
                stderr.Length == 0 ? $"tmux exited {result.ExitCode}" : $"tmux exited {result.ExitCode}: {stderr}");
        }

        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }
}
