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
        return new ServerConnectionOptions(tmuxBinaryPath: context.Executable(context.Environment.GetValueOrDefault("LIBTMUX_TMUX") ?? "tmux"), socketName: name, socketPath: socket, configurationFile: invocation.Text("tmux_config"), colorMode: invocation.Flag("colors256") ? TmuxColorMode.Colors256 : TmuxColorMode.Default, childEnvironment: context.Environment);
    }

    internal async Task<int> LoadAsync()
    {
        if (invocation.Flag("colors88")) throw new CliException("unsupported-color-mode", "88-color mode is unsupported on tmux 3.2a and newer. Use -2 for 256 colors.", 2);
        if (invocation.Machine && !invocation.Flag("detached") && !invocation.Flag("append")) throw new CliException("mode-required", "Machine load requires -d or --append.", 2);
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
        await handoff.ResolveAsync(Connection()).ConfigureAwait(false);
        if (extensions && handoff.Mode == LoadMode.Append) throw new CliException("unsupported-append-extensions", "Append with Python workspace extensions is unsupported. Load the extensions into a separate session with -d.", 2);
        if (extensions && handoff.Mode is LoadMode.Attach or LoadMode.Switch) throw new CliException("unsupported-attached-extensions", "Python extension handoff is not yet supported. Load the extensions with -d.", 2);
        _server = handoff.Server;
        _loadGeneration = _server?.Generation;
        (Session Session, string Name)? appendTarget = handoff.Mode == LoadMode.Append ? (handoff.CurrentSession!, handoff.CurrentSessionName!) : null;
        if (extensions)
            return await new ProcessCommands(context, invocation, output).BridgeLoadAsync(handoff.Mode == LoadMode.Detached).ConfigureAwait(false);
        Session? finalSession = null;
        string columns = Dimension("TMUXP_DEFAULT_COLUMNS", "COLUMNS", 80);
        string rows = Dimension("TMUXP_DEFAULT_ROWS", "ROWS", 24);
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
            async Task<string> Change(IReadOnlyList<string> arguments)
            {
                string result = await Command(arguments).ConfigureAwait(false);
                changed = true;
                return result;
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
                        finalSession = await BindSessionAsync(session, TmuxConnection.ParseGeneration(identity[1])).ConfigureAwait(false);
                        results.Add(Result(index, input.Path, session, input.Plan.Name, "reused"));
                        await output.EventAsync("workspace-completed", new { input_index = index, session_id = session, status = "reused" }).ConfigureAwait(false);
                        if (!invocation.Machine) output.Human("Using existing session " + input.Plan.Name, "success");
                        continue;
                    }
                }
                await output.ProgressAsync(progress => progress.StartWorkspace(input.Plan, sessionName), force: true).ConfigureAwait(false);
                string? bootstrap = null;
                if (session is null)
                {
                    stage = "session-created";
                    List<string> start = ["new-session", "-d", "-s", input.Plan.Name, "-c", input.Plan.Directory, "-x", columns, "-y", rows, "-P", "-F", "#{session_id}\t#{window_id}\t#{pid}:#{start_time}"];
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
                    if (scriptArguments.Length == 0) throw new CliException("invalid-config", "before_script must name an executable.");
                    if (scriptArguments[0].StartsWith('.')) scriptArguments[0] = Path.GetFullPath(scriptArguments[0], Path.GetDirectoryName(input.Path)!);
                    ChildResult child = await ProcessCommands.RunProcessAsync(context, output, scriptArguments[0], scriptArguments[1..], input.Plan.Directory, stream: true).ConfigureAwait(false);
                    if (child.ExitCode != 0) throw new CliException("script-failed", $"before_script exited with status {child.ExitCode}.");
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
                    List<string> args = ["new-window", "-d", "-P", "-F", "#{window_id}\t#{pane_id}", "-t", session + ":" + (appendTarget is null ? window.Index?.ToString(CultureInfo.InvariantCulture) : null)];
                    if (window.Name is not null) args.AddRange(["-n", window.Name]);
                    PaneArguments(args, first);
                    string[] identifiers = (await Change(args).ConfigureAwait(false)).TrimEnd('\n').Split('\t');
                    string windowId = identifiers[0];
                    string paneId = identifiers[1];
                    firstWindow ??= windowId;
                    if (window.Focus) focusedWindow = windowId;
                    foreach (var option in window.Options) await Change(["set-window-option", "-t", windowId, option.Key, OptionValue(option.Value)]).ConfigureAwait(false);
                    await output.EventAsync(stage, new { input_index = index, session_id = session, window_id = windowId, window_name = window.Name }).ConfigureAwait(false);
                    completedStage = stage;
                    string? focusedPane = null;
                    for (int paneIndex = 0; paneIndex < window.Panes.Length; paneIndex++)
                    {
                        PanePlan pane = window.Panes[paneIndex];
                        await output.ProgressAsync(progress => progress.StartPane(paneIndex + 1)).ConfigureAwait(false);
                        if (paneIndex > 0)
                        {
                            List<string> split = ["split-window", "-d", "-P", "-F", "#{pane_id}", "-t", windowId];
                            PaneArguments(split, pane);
                            paneId = (await Change(split).ConfigureAwait(false)).TrimEnd('\n');
                            await Change(["select-layout", "-t", windowId, "tiled"]).ConfigureAwait(false);
                        }
                        await output.EventAsync(stage = "pane-created", new { input_index = index, window_id = windowId, pane_id = paneId }).ConfigureAwait(false);
                        completedStage = stage;
                        if (pane.Focus) focusedPane = paneId;
                        if (pane.Commands.Length > 0 && pane.Shell is null && (input.Plan.Readiness == "always" || (input.Plan.Readiness == "auto" && (await Field(paneId, "pane_current_command").ConfigureAwait(false)).EndsWith("zsh", StringComparison.Ordinal))))
                        {
                            long started = System.Diagnostics.Stopwatch.GetTimestamp();
                            while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
                            {
                                if ((await Command(["display-message", "-p", "-t", paneId, "#{cursor_x},#{cursor_y}"]).ConfigureAwait(false)).TrimEnd('\n') != "0,0") break;
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
                    }
                    stage = "window-finalized";
                    if (window.Layout is not null) await Change(["select-layout", "-t", windowId, window.Layout]).ConfigureAwait(false);
                    foreach (var option in window.OptionsAfter) await Change(["set-window-option", "-t", windowId, option.Key, OptionValue(option.Value)]).ConfigureAwait(false);
                    if (focusedPane is not null) await Change(["select-pane", "-t", focusedPane]).ConfigureAwait(false);
                    completedStage = stage;
                    await output.ProgressAsync(progress => progress.CompleteWindow(), force: true).ConfigureAwait(false);
                }
                stage = "workspace-finalized";
                if (bootstrap is not null) await Change(["kill-window", "-t", bootstrap]).ConfigureAwait(false);
                if ((focusedWindow ?? firstWindow) is string active) await Change(["select-window", "-t", active]).ConfigureAwait(false);
                results.Add(Result(index, input.Path, session, sessionName, created ? "created" : "appended"));
                await output.EventAsync(stage = "workspace-completed", new { input_index = index, session_id = session }).ConfigureAwait(false);
                if (!invocation.Machine) { output.Human("Loaded ", "success", false); output.Human(sessionName, "subject"); }
            }
            catch (Exception failure) when (failure is CliException or OperationCanceledException or LibTmuxException or StaleServerGenerationException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                bool removed = false;
                string? cleanupError = null;
                if (stage == "before-script" && ownedSession is not null)
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
                string code = failure is CliException cli ? cli.Code : failure is OperationCanceledException ? "cancelled" : failure is StaleServerGenerationException ? "stale-server" : failure is IOException or UnauthorizedAccessException ? "output-failed" : "tmux-failed";
                errors.Add(new JsonObject { ["code"] = code, ["message"] = failure.Message, ["input_index"] = index, ["completed_stage"] = completedStage, ["failed_stage"] = stage, ["session_id"] = session, ["created"] = created, ["changed"] = changed, ["removed"] = removed });
                if (cleanupError is not null) errors[^1]!["cleanup_error"] = cleanupError;
                var summary = new { schema_version = 1, command = "load", status = results.Count > 0 || (changed && !removed) ? "partial" : "error", results, errors };
                using CancellationTokenSource reporting = new(TimeSpan.FromSeconds(3));
                try
                {
                    await output.EventAsync("failed", summary, reporting.Token).ConfigureAwait(false);
                    if (!invocation.Flag("ndjson")) await output.ResultAsync(summary, cancellationToken: reporting.Token).ConfigureAwait(false);
                    await output.DiagnosticAsync(code, failure.Message).ConfigureAwait(false);
                }
                catch (Exception interrupted) when (interrupted is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    await output.DiagnosticAsync(code, failure.Message, summary).ConfigureAwait(false);
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
            string code = failure is CliException cli ? cli.Code : failure is OperationCanceledException ? "cancelled" : failure is StaleServerGenerationException ? "stale-server" : failure is IOException or UnauthorizedAccessException ? "output-failed" : "attach-failed";
            string recorded = "Recorded load results:\n" + string.Join("\n", results.Select(result => $"  {result!["session_name"]} ({result["session_id"]}, {result["status"]})"));
            await output.DiagnosticAsync(code, failure.Message, completed, recorded).ConfigureAwait(false);
            return failure is OperationCanceledException ? 130 : failure is CliException status ? status.ExitCode : 1;
        }
        return 0;
    }

    internal async Task FreezeAsync()
    {
        string? supplied = invocation.Many("sessions").FirstOrDefault();
        string? target = supplied is null ? context.Environment.GetValueOrDefault("TMUX_PANE") : await NamedSessionTarget(supplied).ConfigureAwait(false);
        if (target is null)
        {
            string[] sessions = (await Command(["list-sessions", "-F", "#{session_id}"]).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            target = sessions.Length == 1 ? sessions[0] : await NamedSessionTarget(new ReadCommands(context, invocation, output).Prompt("Session name: ")).ConfigureAwait(false);
        }
        string session = await Field(target, "session_id").ConfigureAwait(false);
        JsonObject document = new() { ["session_name"] = await Field(session, "session_name").ConfigureAwait(false) };
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
            captured["options"] = options;
            JsonArray panes = [];
            foreach (string pane in (await Command(["list-panes", "-t", window, "-F", "#{pane_id}"]).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string currentCommand = await Field(pane, "pane_current_command").ConfigureAwait(false);
                bool skipCommand = currentCommand.StartsWith('-') || InterpreterSuffixes.Any(suffix => currentCommand.EndsWith(suffix, StringComparison.Ordinal));
                panes.Add(new JsonObject { ["start_directory"] = await Field(pane, "pane_current_path").ConfigureAwait(false), ["focus"] = await Field(pane, "pane_active").ConfigureAwait(false) == "1", ["shell_command"] = skipCommand ? new JsonArray() : new JsonArray(currentCommand) });
            }
            captured["panes"] = panes;
            windows.Add(captured);
        }
        document["windows"] = windows;
        string format = invocation.Text("workspace_format") ?? "yaml";
        new ReadCommands(context, invocation, output).SaveOrPrint(document, format, document["session_name"] + "." + format);
        if (!invocation.Flag("ndjson") && invocation.Text("save_to") is null) await output.WarningAsync("capture-lossy", "Capture preserves current commands and window options. Original command arguments, history, hooks and plugin state cannot be recovered.").ConfigureAwait(false);
    }

    private static JsonObject Result(int index, string path, string session, string name, string status) => new() { ["input_index"] = index, ["input"] = path, ["session_id"] = session, ["session_name"] = name, ["status"] = status, ["completed_stage"] = "workspace-completed" };

    private static void PaneArguments(List<string> arguments, PanePlan pane)
    {
        if (pane.Directory is not null) arguments.AddRange(["-c", pane.Directory]);
        foreach (var variable in pane.Environment) arguments.AddRange(["-e", variable.Key + "=" + variable.Value]);
        if (pane.Shell is not null) arguments.Add(pane.Shell);
    }

    private static string OptionValue(string value) => value switch { "true" => "on", "false" => "off", _ => value };

    private string Dimension(string primary, string fallback, int value)
    {
        string raw = context.Environment.GetValueOrDefault(primary) ?? context.Environment.GetValueOrDefault(fallback) ?? value.ToString(CultureInfo.InvariantCulture);
        if (!int.TryParse(raw, CultureInfo.InvariantCulture, out int parsed) || parsed is < 1 or > 65535) throw new CliException("invalid-dimension", primary + " must be an integer from 1 through 65535.", 2);
        return raw;
    }

    private async Task<string> Field(string target, string field) => (await Command(["display-message", "-p", "-t", target, "#{" + field + "}"]).ConfigureAwait(false)).TrimEnd('\n');

    private async Task<string> NamedSessionTarget(string name)
    {
        TmuxCommandResult exists = await Server.ExecuteCommandAsync(["has-session", "-t", "=" + name], context.CancellationToken).ConfigureAwait(false);
        if (exists.ExitCode != 0) throw new CliException("session-not-found", $"Session '{name}' was not found.");
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
        if (result.ExitCode != 0) throw new CliException("tmux-failed", string.Join("\n", result.StandardErrorLines));
        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }
}
