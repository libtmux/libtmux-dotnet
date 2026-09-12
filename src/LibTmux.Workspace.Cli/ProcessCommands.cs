using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace LibTmux.Workspace.Cli;

internal sealed record ChildResult(int ExitCode, string StandardOutput, string StandardError, bool Truncated);

internal sealed class ProcessCommands(CliContext context, Invocation invocation, Output output)
{
    internal async Task<int> EditAsync()
    {
        string path = new DocumentStore(context).Resolve(invocation.Many("files")[0]);
        string[] editor = SplitArguments(context.Environment.GetValueOrDefault("EDITOR") ?? "vim");
        if (editor.Length == 0) throw new CliException("editor-required", "EDITOR must name an executable.");
        ChildResult result = await RunProcessAsync(context, output, editor[0], [.. editor.Skip(1), path], context.Directory, stream: false, interactive: !invocation.Machine).ConfigureAwait(false);
        output.Result(new { schema_version = 1, command = "edit", status = result.ExitCode == 0 ? "ok" : "error", path, child_status = result.ExitCode, stdout = result.StandardOutput, stderr = result.StandardError, truncated = result.Truncated }, $"Editor exited with status {result.ExitCode}.");
        return result.ExitCode;
    }

    internal async Task DebugAsync()
    {
        string Mask(string value) => string.IsNullOrEmpty(context.Home) ? value : value.Replace(context.Home, "~", StringComparison.Ordinal);
        JsonObject tmux = [];
        ChildResult version = await RunProcessAsync(context, output, "tmux", ["-V"], context.Directory, stream: false).ConfigureAwait(false);
        tmux["version"] = version.StandardOutput.TrimEnd();
        tmux["status"] = version.ExitCode;
        JsonObject result = new()
        {
            ["schema_version"] = 1,
            ["port"] = ".NET",
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["platform"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["working_directory"] = Mask(context.Directory),
            ["config_directories"] = new JsonArray(new DocumentStore(context).Candidates().Select(path => JsonValue.Create(Mask(path))).ToArray()),
            ["tmux"] = tmux,
            ["redaction"] = "Home is masked in named paths. Environment values and raw server options are omitted.",
        };
        if (invocation.Machine) output.Result(result);
        else foreach (var item in result) { output.Human(item.Key + ": ", "heading", false); output.Human(item.Value?.ToString() ?? "", "information"); }
    }

    internal async Task<int> ShellAsync()
    {
        bool interactive = invocation.Text("code") is null;
        if (interactive && (invocation.Machine || !context.Terminal)) throw new CliException("terminal-required", "An interactive Python shell requires a terminal. Use -c for machine output.", 2);
        string python = await PythonAsync().ConfigureAwait(false);
        string[] args = StripExtensions(invocation.Arguments);
        ChildResult result = await RunProcessAsync(context, output, python, BridgeArguments(args), context.Directory, stream: !interactive, interactive: interactive).ConfigureAwait(false);
        var summary = new { schema_version = 1, command = "shell", status = result.ExitCode == 0 ? "ok" : "error", child_status = result.ExitCode, stdout = result.StandardOutput, stderr = result.StandardError, truncated = result.Truncated, encoding = "utf-8-replacement" };
        if (invocation.Flag("ndjson")) output.Event(result.ExitCode == 0 ? "completed" : "failed", summary);
        else output.Result(summary, result.StandardOutput);
        return result.ExitCode;
    }

    internal async Task<int> BridgeLoadAsync()
    {
        string python = await PythonAsync().ConfigureAwait(false);
        output.Event("started", new { bridge = "tmuxp", version = "1.74.0" });
        ChildResult result = await RunProcessAsync(context, output, python, BridgeArguments(StripExtensions(invocation.Arguments)), context.Directory, stream: true).ConfigureAwait(false);
        var summary = new { schema_version = 1, command = "load", status = result.ExitCode == 0 ? "ok" : "error", results = new[] { new { bridge = "tmuxp", child_status = result.ExitCode, stdout = result.StandardOutput, stderr = result.StandardError, truncated = result.Truncated } }, errors = result.ExitCode == 0 ? Array.Empty<object>() : [new { code = "bridge-failed", message = "Python workspace load failed." }] };
        if (invocation.Flag("ndjson")) output.Event(result.ExitCode == 0 ? "completed" : "failed", summary);
        else output.Result(summary);
        return result.ExitCode;
    }

    internal async Task<int> AttachAsync(string target, ServerConnectionOptions options)
    {
        if (!context.Terminal) throw new CliException("terminal-required", "Attach requires a terminal. Use -d to load without attaching.");
        List<string> args = [];
        if (options.SocketPath is string path) args.AddRange(["-S", path]);
        if (options.SocketName is string name) args.AddRange(["-L", name]);
        args.AddRange([context.Environment.ContainsKey("TMUX") ? "switch-client" : "attach-session", "-t", target]);
        return (await RunProcessAsync(context, output, "tmux", args, context.Directory, stream: false, interactive: true).ConfigureAwait(false)).ExitCode;
    }

    private async Task<string> PythonAsync()
    {
        string python = context.Environment.GetValueOrDefault("TMUX_WORKSPACE_PYTHON") ?? "python3";
        ChildResult check = await RunProcessAsync(context, output, python, ["-c", "import importlib.metadata; print(importlib.metadata.version('tmuxp'))"], context.Directory, stream: false).ConfigureAwait(false);
        if (check.ExitCode != 0 || check.StandardOutput.Trim() != "1.74.0") throw new CliException("unsupported-runtime", "Python compatibility requires tmuxp 1.74.0. Set TMUX_WORKSPACE_PYTHON to its Python executable.");
        return python;
    }

    private static string[] StripExtensions(string[] args)
    {
        List<string> result = [];
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] is "--json" or "--ndjson") continue;
            if (args[index] == "--color") { index++; continue; }
            if (args[index].StartsWith("--color=", StringComparison.Ordinal)) continue;
            result.Add(args[index]);
        }
        return result.ToArray();
    }

    private static string[] BridgeArguments(string[] args) => ["-u", "-c", "from tmuxp.cli import cli; import sys; cli(sys.argv[1:])", .. args];

    internal static async Task<ChildResult> RunProcessAsync(CliContext context, Output output, string executable, IReadOnlyList<string> arguments, string directory, bool stream, bool interactive = false)
    {
        ProcessStartInfo start = new(context.Executable(executable)) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = !interactive, RedirectStandardError = !interactive, RedirectStandardInput = !interactive, StandardOutputEncoding = interactive ? null : Encoding.UTF8, StandardErrorEncoding = interactive ? null : Encoding.UTF8 };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var variable in context.Environment) start.Environment[variable.Key] = variable.Value;
        using Process process = new() { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception failure) { throw new CliException("executable-unavailable", $"Cannot start '{executable}': {failure.Message}"); }
        if (!interactive) process.StandardInput.Close();
        object sync = new();
        bool truncated = false;
        async Task<string> Drain(StreamReader reader, string channel)
        {
            StringBuilder retained = new();
            char[] buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, context.CancellationToken).ConfigureAwait(false)) > 0)
            {
                string value = new(buffer, 0, count);
                lock (sync)
                {
                    int available = Math.Max(0, 65536 - retained.Length);
                    retained.Append(value.AsSpan(0, Math.Min(count, available)));
                    if (count > available) truncated = true;
                    if (stream)
                    {
                        output.Event("script-output", new { stream = channel, text = value, encoding = "utf-8-replacement" });
                        if (!output.Machine) output.Human(value, "secondary", newline: false, writer: context.Error);
                    }
                }
            }
            return retained.ToString();
        }
        Task<string> stdout = interactive ? Task.FromResult("") : Drain(process.StandardOutput, "stdout");
        Task<string> stderr = interactive ? Task.FromResult("") : Drain(process.StandardError, "stderr");
        Task exited = process.WaitForExitAsync(context.CancellationToken);
        try
        {
            List<Task> pending = [stdout, stderr, exited];
            while (pending.Count > 0)
            {
                Task finished = await Task.WhenAny(pending).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
                pending.Remove(finished);
            }
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (Exception failure) when (failure is OperationCanceledException or IOException) { }
            throw;
        }
        return new ChildResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), truncated);
    }

    internal static string[] SplitArguments(string text)
    {
        List<string> result = [];
        StringBuilder word = new();
        char quote = '\0';
        bool escaped = false;
        bool started = false;
        foreach (char character in text)
        {
            if (escaped) { word.Append(character); escaped = false; started = true; }
            else if (character == '\\' && quote != '\'') { escaped = true; started = true; }
            else if (quote != '\0') { if (character == quote) quote = '\0'; else word.Append(character); }
            else if (character is '\'' or '"') { quote = character; started = true; }
            else if (char.IsWhiteSpace(character)) { if (started) { result.Add(word.ToString()); word.Clear(); started = false; } }
            else { word.Append(character); started = true; }
        }
        if (escaped || quote != '\0') throw new CliException("invalid-editor", "EDITOR contains an unfinished quote or escape.", 2);
        if (started) result.Add(word.ToString());
        return result.ToArray();
    }
}
