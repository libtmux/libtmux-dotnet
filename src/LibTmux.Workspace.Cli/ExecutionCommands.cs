using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;

namespace LibTmux.Workspace.Cli;

[UnsupportedOSPlatform("windows")]
internal sealed class ExecutionCommands(CliContext context, Invocation invocation, Output output)
{
    private static readonly string[] InterpreterSuffixes = ["python", "ruby", "node"];
    private readonly DocumentStore _documents = new(context);
    private Server? _server;
    private Server Server => _server ??= Server.Open(Connection());

    internal ServerConnectionOptions Connection()
    {
        string? socket = invocation.Text("socket_path");
        string? name = invocation.Text("socket_name");
        if (socket is null && name is null) socket = CurrentSocket(out _);
        if (socket is not null) socket = Path.GetFullPath(socket, context.Directory);
        return new ServerConnectionOptions(tmuxBinaryPath: context.Executable(context.Environment.GetValueOrDefault("LIBTMUX_TMUX") ?? "tmux"), socketName: name, socketPath: socket, configurationFile: invocation.Text("tmux_config"), colorMode: invocation.Flag("colors256") ? TmuxColorMode.Colors256 : TmuxColorMode.Default, childEnvironment: context.Environment);
    }

    private string? CurrentSocket(out int processId)
    {
        processId = 0;
        if (context.Environment.GetValueOrDefault("TMUX") is not string current) return null;
        int last = current.LastIndexOf(',');
        int previous = last > 0 ? current.LastIndexOf(',', last - 1) : -1;
        if (previous <= 0
            || !int.TryParse(current.AsSpan(previous + 1, last - previous - 1), NumberStyles.None, CultureInfo.InvariantCulture, out processId)
            || processId <= 0
            || !int.TryParse(current.AsSpan(last + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int session)
            || session < 0) return null;
        return current[..previous];
    }

    private async Task<string?> AppendTargetAsync()
    {
        if (!invocation.Flag("append")) return null;
        string? pane = context.Environment.GetValueOrDefault("TMUX_PANE");
        string? socket = CurrentSocket(out int processId);
        if (socket is null || !PaneId.TryParse(pane, out _)) throw new CliException("session-required", "Append requires TMUX and TMUX_PANE from a current tmux session.");
        Server current = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: Connection().TmuxBinaryPath, socketPath: Path.GetFullPath(socket, context.Directory), childEnvironment: context.Environment));
        string[] query = ["display-message", "-p", "-t", pane!, "#{pid}:#{start_time}"];
        TmuxCommandResult identity = await current.ExecuteCommandAsync(query, context.CancellationToken).ConfigureAwait(false);
        if (identity.ExitCode != 0) throw new CliException("session-required", "The current tmux pane is unavailable.");
        string generation = Encoding.UTF8.GetString(identity.StandardOutput.Span).TrimEnd('\n');
        if (!generation.StartsWith(processId.ToString(CultureInfo.InvariantCulture) + ":", StringComparison.Ordinal)) throw new CliException("stale-environment", "The server recorded in TMUX has been replaced.");
        if (generation != (await Command(query).ConfigureAwait(false)).TrimEnd('\n')) throw new CliException("endpoint-mismatch", "Append cannot select a different server from the current tmux pane.");
        return pane;
    }

    internal async Task<int> LoadAsync()
    {
        if (invocation.Machine && !invocation.Flag("detached") && !invocation.Flag("append")) throw new CliException("mode-required", "Machine load requires -d or --append.", 2);
        var inputs = invocation.Many("files").Select(file =>
        {
            string path = _documents.Resolve(file);
            JsonObject document = DocumentStore.Read(path);
            return (Path: path, Document: document, Plan: WorkspacePlan.Parse(document, path, _documents, invocation.Text("session_name")));
        }).ToArray();
        string? appendTarget = await AppendTargetAsync().ConfigureAwait(false);
        if (inputs.Any(input => input.Document["plugins"] is not null || input.Document["workspace_builder"] is not null))
            return await new ProcessCommands(context, invocation, output).BridgeLoadAsync().ConfigureAwait(false);
        string columns = Dimension("TMUXP_DEFAULT_COLUMNS", "COLUMNS", 80);
        string rows = Dimension("TMUXP_DEFAULT_ROWS", "ROWS", 24);
        JsonArray results = [];
        JsonArray errors = [];
        output.Event("started", new { inputs = inputs.Length });
        string stage = "workspace-started";
        for (int index = 0; index < inputs.Length; index++)
        {
            var input = inputs[index];
            string? session = null;
            bool created = false;
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                output.Event(stage = "workspace-started", new { input_index = index, input = input.Path });
                if (appendTarget is not null) session = await Field(appendTarget, "session_id").ConfigureAwait(false);
                else
                {
                    TmuxCommandResult exists = await Server.ExecuteCommandAsync(["has-session", "-t", "=" + input.Plan.Name], context.CancellationToken).ConfigureAwait(false);
                    if (exists.ExitCode == 0)
                    {
                        session = await Field("=" + input.Plan.Name, "session_id").ConfigureAwait(false);
                        results.Add(Result(index, input.Path, session, input.Plan.Name, "reused"));
                        output.Event("workspace-completed", new { input_index = index, session_id = session, status = "reused" });
                        continue;
                    }
                }
                string? bootstrap = null;
                if (session is null)
                {
                    stage = "session-created";
                    List<string> start = ["new-session", "-d", "-s", input.Plan.Name, "-c", input.Plan.Directory, "-x", columns, "-y", rows, "-P", "-F", "#{session_id}\t#{window_id}"];
                    foreach (var variable in input.Plan.Environment) start.AddRange(["-e", variable.Key + "=" + variable.Value]);
                    string[] identifiers = (await Command(start).ConfigureAwait(false)).TrimEnd('\n').Split('\t');
                    session = identifiers[0];
                    bootstrap = identifiers[1];
                    created = true;
                    int temporaryIndex = 99999;
                    while (input.Plan.Windows.Any(window => window.Index == temporaryIndex)) temporaryIndex++;
                    await Command(["move-window", "-s", bootstrap, "-t", session + ":" + temporaryIndex.ToString(CultureInfo.InvariantCulture)]).ConfigureAwait(false);
                    output.Event(stage, new { input_index = index, session_id = session, session_name = input.Plan.Name });
                }
                if (input.Plan.BeforeScript is string script)
                {
                    stage = "before-script";
                    string[] scriptArguments = ProcessCommands.SplitArguments(script);
                    if (scriptArguments.Length == 0) throw new CliException("invalid-config", "before_script must name an executable.");
                    if (scriptArguments[0].StartsWith('.')) scriptArguments[0] = Path.GetFullPath(scriptArguments[0], Path.GetDirectoryName(input.Path)!);
                    ChildResult child = await ProcessCommands.RunProcessAsync(context, output, scriptArguments[0], scriptArguments[1..], input.Plan.Directory, stream: true).ConfigureAwait(false);
                    if (child.ExitCode != 0) throw new CliException("script-failed", $"before_script exited with status {child.ExitCode}.");
                }
                foreach (var option in input.Plan.GlobalOptions) await Command(["set-option", "-g", option.Key, OptionValue(option.Value)]).ConfigureAwait(false);
                foreach (var option in input.Plan.Options) await Command(["set-option", "-t", session, option.Key, OptionValue(option.Value)]).ConfigureAwait(false);
                foreach (var variable in input.Plan.Environment) await Command(["set-environment", "-t", session, variable.Key, variable.Value]).ConfigureAwait(false);
                string? focusedWindow = null;
                string? firstWindow = null;
                foreach (WindowPlan window in input.Plan.Windows)
                {
                    stage = "window-created";
                    PanePlan first = window.Panes[0];
                    List<string> args = ["new-window", "-d", "-P", "-F", "#{window_id}\t#{pane_id}", "-t", session + ":" + (appendTarget is null ? window.Index?.ToString(CultureInfo.InvariantCulture) : null)];
                    if (window.Name is not null) args.AddRange(["-n", window.Name]);
                    PaneArguments(args, first);
                    string[] identifiers = (await Command(args).ConfigureAwait(false)).TrimEnd('\n').Split('\t');
                    string windowId = identifiers[0];
                    string paneId = identifiers[1];
                    firstWindow ??= windowId;
                    if (window.Focus) focusedWindow = windowId;
                    foreach (var option in window.Options) await Command(["set-window-option", "-t", windowId, option.Key, OptionValue(option.Value)]).ConfigureAwait(false);
                    output.Event(stage, new { input_index = index, session_id = session, window_id = windowId, window_name = window.Name });
                    string? focusedPane = null;
                    for (int paneIndex = 0; paneIndex < window.Panes.Length; paneIndex++)
                    {
                        PanePlan pane = window.Panes[paneIndex];
                        if (paneIndex > 0)
                        {
                            List<string> split = ["split-window", "-d", "-P", "-F", "#{pane_id}", "-t", windowId];
                            PaneArguments(split, pane);
                            paneId = (await Command(split).ConfigureAwait(false)).TrimEnd('\n');
                            await Command(["select-layout", "-t", windowId, "tiled"]).ConfigureAwait(false);
                        }
                        output.Event(stage = "pane-created", new { input_index = index, window_id = windowId, pane_id = paneId });
                        if (pane.Focus) focusedPane = paneId;
                        if (pane.Shell is null && (input.Plan.Readiness == "always" || (input.Plan.Readiness == "auto" && (await Field(paneId, "pane_current_command").ConfigureAwait(false)).EndsWith("zsh", StringComparison.Ordinal))))
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
                            await Command(["send-keys", "-t", paneId, "-l", "--", command.Text]).ConfigureAwait(false);
                            if (command.Enter) await Command(["send-keys", "-t", paneId, "Enter"]).ConfigureAwait(false);
                            await Task.Delay(TimeSpan.FromSeconds(command.After), context.CancellationToken).ConfigureAwait(false);
                        }
                    }
                    if (window.Layout is not null) await Command(["select-layout", "-t", windowId, window.Layout]).ConfigureAwait(false);
                    foreach (var option in window.OptionsAfter) await Command(["set-window-option", "-t", windowId, option.Key, OptionValue(option.Value)]).ConfigureAwait(false);
                    if (focusedPane is not null) await Command(["select-pane", "-t", focusedPane]).ConfigureAwait(false);
                }
                if (bootstrap is not null) await Command(["kill-window", "-t", bootstrap]).ConfigureAwait(false);
                if ((focusedWindow ?? firstWindow) is string active) await Command(["select-window", "-t", active]).ConfigureAwait(false);
                results.Add(Result(index, input.Path, session, input.Plan.Name, created ? "created" : "appended"));
                output.Event(stage = "workspace-completed", new { input_index = index, session_id = session });
                if (!invocation.Machine) { output.Human("Loaded ", "success", false); output.Human(input.Plan.Name, "subject"); }
            }
            catch (Exception failure) when (failure is CliException or OperationCanceledException or LibTmuxException or ArgumentException)
            {
                bool removed = false;
                if (stage == "before-script" && created && session is not null)
                {
                    using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(3));
                    TmuxCommandResult deletion = await Server.ExecuteCommandAsync(["kill-session", "-t", session], cleanup.Token).ConfigureAwait(false);
                    removed = deletion.ExitCode == 0;
                }
                string code = failure is CliException cli ? cli.Code : failure is OperationCanceledException ? "cancelled" : "tmux-failed";
                errors.Add(new JsonObject { ["code"] = code, ["message"] = failure.Message, ["input_index"] = index, ["failed_stage"] = stage, ["session_id"] = session, ["created"] = created, ["removed"] = removed });
                var summary = new { schema_version = 1, command = "load", status = results.Count > 0 || (created && !removed) ? "partial" : "error", results, errors };
                if (invocation.Flag("ndjson")) output.Event("failed", summary);
                else output.Result(summary);
                output.Diagnostic(code, failure.Message);
                return failure is OperationCanceledException ? 130 : 1;
            }
        }
        var completed = new { schema_version = 1, command = "load", status = "ok", results, errors };
        if (invocation.Flag("ndjson")) output.Event("completed", completed);
        else if (invocation.Machine) output.Result(completed);
        if (!invocation.Flag("detached") && !invocation.Flag("append") && results.LastOrDefault()?["session_id"]?.ToString() is string target)
            return await new ProcessCommands(context, invocation, output).AttachAsync(target, Connection()).ConfigureAwait(false);
        return 0;
    }

    internal async Task FreezeAsync()
    {
        string? target = invocation.Many("sessions").FirstOrDefault() ?? context.Environment.GetValueOrDefault("TMUX_PANE");
        if (target is null)
        {
            string[] sessions = (await Command(["list-sessions", "-F", "#{session_id}"]).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            target = sessions.Length == 1 ? sessions[0] : new ReadCommands(context, invocation, output).Prompt("Session name: ");
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
        if (!invocation.Flag("ndjson") && invocation.Text("save_to") is null) output.Warning("capture-lossy", "Capture preserves current commands and window options. Original command arguments, history, hooks and plugin state cannot be recovered.");
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

    private async Task<string> Command(IReadOnlyList<string> arguments)
    {
        TmuxCommandResult result = await Server.ExecuteCommandAsync(invocation.Flag("colors88") ? ["-8", .. arguments] : arguments, context.CancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new CliException("tmux-failed", string.Join("\n", result.StandardErrorLines));
        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }
}
