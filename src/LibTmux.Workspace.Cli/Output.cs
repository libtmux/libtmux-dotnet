using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace LibTmux.Workspace.Cli;

internal sealed record CliContext(TextWriter Output, TextWriter Error, string Directory, IReadOnlyDictionary<string, string?> Environment, CancellationToken CancellationToken)
{
    internal string Home => Environment.GetValueOrDefault("HOME") ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
    internal bool Terminal => ReferenceEquals(Output, Console.Out) && !Console.IsOutputRedirected;
    internal bool ErrorTerminal => ReferenceEquals(Error, Console.Error) && !Console.IsErrorRedirected;

    internal string Executable(string name)
    {
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar)) return Path.GetFullPath(name, Directory);
        foreach (string folder in (Environment.GetValueOrDefault("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.GetFullPath(Path.Combine(folder, name), Directory);
            if (File.Exists(candidate) && !System.IO.Directory.Exists(candidate) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)) return candidate;
        }
        throw new CliException("executable-unavailable", $"Executable '{name}' was not found on PATH.");
    }
}

internal sealed class CliException(string code, string message, int exitCode = 1) : Exception(message)
{
    internal string Code { get; } = code;
    internal int ExitCode { get; } = exitCode;
}

internal sealed class Output(CliContext context, Invocation invocation, TextWriter? log = null, ProgressDisplay? progress = null) : IAsyncDisposable
{
    private int _sequence;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private TextWriter? _log = log;
    private ProgressDisplay? _progress = progress;
    private long _lastDraw;
    private bool _partialOutput, _partialError;
    internal bool Machine => invocation.Machine;

    internal void PrepareProgress()
    {
        if (context.ErrorTerminal && ProgressDisplay.ErrorSize() is { } size && ProgressOptions.Resolve(invocation, context.Environment, true) is { } options)
            _progress = new ProgressDisplay(options, size.Width, size.Height, UseColor(context.Error), ProgressDisplay.ErrorSize);
    }

    internal async ValueTask ProgressAsync(Action<ProgressDisplay> update, bool force = false)
    {
        if (_progress is null) return;
        await _writes.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            if (_progress is not null) update(_progress);
            await DrawProgressAsync(context.CancellationToken, force).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    internal async ValueTask FinishProgressAsync()
    {
        await _writes.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try { await ClearProgressAsync(context.CancellationToken).ConfigureAwait(false); await FreshLinesAsync(context.CancellationToken).ConfigureAwait(false); }
        finally { _writes.Release(); }
    }

    private void CheckProgressSize()
    {
        if (_progress is null || _progress.SizeUnchanged) return;
        _progress.Dispose();
        _progress = null;
    }

    private async ValueTask DrawProgressAsync(CancellationToken token, bool force = false)
    {
        CheckProgressSize();
        if (_progress is null) return;
        if (!force && !_progress.WorkComplete && _progress.Rows > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastDraw) < TimeSpan.FromMilliseconds(50)) return;
        await FreshLinesAsync(token).ConfigureAwait(false);
        string frame = _progress.Render();
        await context.Error.WriteAsync((_progress.ClearSequence + frame).AsMemory(), token).ConfigureAwait(false);
        _progress.Rows = frame.Count(character => character == '\n');
        await context.Error.FlushAsync(token).ConfigureAwait(false);
        _lastDraw = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private async ValueTask ClearProgressAsync(CancellationToken token)
    {
        CheckProgressSize();
        if (_progress is not { Rows: > 0 }) return;
        await context.Error.WriteAsync(_progress.ClearSequence.AsMemory(), token).ConfigureAwait(false);
        _progress.Rows = 0;
        await context.Error.FlushAsync(token).ConfigureAwait(false);
    }

    private async ValueTask FreshLinesAsync(CancellationToken token)
    {
        if (_partialOutput && context.Terminal)
        {
            await context.Output.WriteAsync("\r\n".AsMemory(), token).ConfigureAwait(false);
            await context.Output.FlushAsync(token).ConfigureAwait(false);
            _partialOutput = false;
        }
        if (_partialError && context.ErrorTerminal)
        {
            await context.Error.WriteAsync("\r\n".AsMemory(), token).ConfigureAwait(false);
            await context.Error.FlushAsync(token).ConfigureAwait(false);
            _partialError = false;
        }
    }

    internal void Result(object? value, string? status = null)
    {
        if (Machine) Json(context.Output, value);
        else Human(status ?? JsonSerializer.Serialize(value), "success");
    }

    internal async ValueTask ResultAsync(object? value, string? status = null, CancellationToken? cancellationToken = null)
    {
        if (Machine) await JsonAsync(context.Output, value, cancellationToken ?? context.CancellationToken).ConfigureAwait(false);
        else Human(status ?? JsonSerializer.Serialize(value), "success");
    }

    internal void Records(IEnumerable<JsonObject> records, string? collection = null, JsonArray? directories = null)
    {
        JsonObject[] rows = records.ToArray();
        if (invocation.Flag("ndjson")) foreach (JsonObject row in rows) Json(context.Output, row);
        else if (invocation.Flag("json")) Result(collection is null ? new JsonArray(rows.Select(row => row.DeepClone()).ToArray()) : new JsonObject { [collection] = new JsonArray(rows.Select(row => row.DeepClone()).ToArray()), ["global_workspace_dirs"] = directories });
        else
        {
            string? directory = null;
            foreach (JsonObject row in rows)
            {
                string? path = row["path"]?.ToString();
                if (invocation.Flag("tree") && Path.GetDirectoryName(path) is string parent && parent != directory)
                {
                    Human(parent, "heading");
                    directory = parent;
                }
                Human(row["name"]?.ToString() ?? "", "subject", newline: false);
                Human("  " + path, "information");
            }
        }
    }

    internal async ValueTask EventAsync(string name, object? data = null, CancellationToken? cancellationToken = null)
    {
        CancellationToken token = cancellationToken ?? context.CancellationToken;
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try { await EventCoreAsync(name, data, token).ConfigureAwait(false); }
        finally { _writes.Release(); }
    }

    private async ValueTask EventCoreAsync(string name, object? data, CancellationToken token)
    {
        if (name is "workspace-completed" or "completed" or "failed")
        {
            if (_progress is { Rows: > 0 }) await DrawProgressAsync(token, force: true).ConfigureAwait(false);
            await ClearProgressAsync(token).ConfigureAwait(false);
            await FreshLinesAsync(token).ConfigureAwait(false);
            _progress?.Reset();
        }
        int sequence = ++_sequence;
        string severity = name == "script-output" ? "debug" : name == "failed" ? "error" : "info";
        await LogAsync(new { schema_version = 1, command = invocation.Command, @event = name, sequence, data, severity }, severity, token).ConfigureAwait(false);
        if (invocation.Flag("ndjson")) await JsonAsync(context.Output, new { schema_version = 1, command = invocation.Command, @event = name, sequence, data }, token).ConfigureAwait(false);
    }

    internal async ValueTask ScriptOutputAsync(string channel, string text)
    {
        await _writes.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            await EventCoreAsync("script-output", new { stream = channel, text, encoding = "utf-8-replacement" }, context.CancellationToken).ConfigureAwait(false);
            if (!Machine)
            {
                CheckProgressSize();
                if (_progress is { Panel: true })
                {
                    _progress.Script(channel, text);
                    await DrawProgressAsync(context.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ClearProgressAsync(context.CancellationToken).ConfigureAwait(false);
                    TextWriter destination = channel == "stdout" ? context.Output : context.Error;
                    await destination.WriteAsync(text.AsMemory(), context.CancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(context.CancellationToken).ConfigureAwait(false);
                    if (text.Length > 0)
                    {
                        bool partial = text[^1] is not '\r' and not '\n';
                        if (channel == "stdout") _partialOutput = partial;
                        else _partialError = partial;
                    }
                }
            }
        }
        finally { _writes.Release(); }
    }

    internal async ValueTask DiagnosticAsync(string code, string message, object? effects = null)
    {
        using CancellationTokenSource reporting = new(TimeSpan.FromSeconds(3));
        bool entered = false;
        try
        {
            await _writes.WaitAsync(reporting.Token).ConfigureAwait(false);
            entered = true;
            try { await ClearProgressAsync(reporting.Token).ConfigureAwait(false); await FreshLinesAsync(reporting.Token).ConfigureAwait(false); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or OperationCanceledException) { }
            await LogAsync(new { schema_version = 1, command = invocation.Command, code, message, severity = "error" }, "error", reporting.Token).ConfigureAwait(false);
            if (Machine) await JsonAsync(context.Error, effects is null ? (object)new { code, message } : new { code, message, effects }, reporting.Token).ConfigureAwait(false);
            else Human(message, "error", writer: context.Error);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        finally { if (entered) _writes.Release(); }
    }

    internal async ValueTask WarningAsync(string code, string message)
    {
        if (!Enabled("warning")) return;
        await _writes.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            await LogAsync(new { schema_version = 1, command = invocation.Command, code, message, severity = "warning" }, "warning", context.CancellationToken).ConfigureAwait(false);
            await WarningCoreAsync(code, message, context.CancellationToken).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    private async ValueTask WarningCoreAsync(string code, string message, CancellationToken token)
    {
        await ClearProgressAsync(token).ConfigureAwait(false);
        await FreshLinesAsync(token).ConfigureAwait(false);
        if (Machine) await JsonAsync(context.Error, new { code, message, severity = "warning" }, token).ConfigureAwait(false);
        else Human(message, "warning", writer: context.Error);
    }

    private bool Enabled(string severity)
    {
        static int Rank(string level) => level switch { "debug" => 0, "info" => 1, "warning" => 2, "error" => 3, "critical" => 4, _ => 2 };
        return Rank(severity) >= Rank(invocation.Text("log_level") ?? "warning");
    }

    private async ValueTask LogAsync(object record, string severity, CancellationToken token)
    {
        if (_log is not TextWriter writer || !Enabled(severity)) return;
        try { await JsonAsync(writer, record, token).ConfigureAwait(false); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            _log = null;
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception closing) when (closing is IOException or UnauthorizedAccessException) { }
            await ReportLogFailureAsync(failure).ConfigureAwait(false);
        }
    }

    private async ValueTask ReportLogFailureAsync(Exception failure)
    {
        using CancellationTokenSource reporting = new(TimeSpan.FromSeconds(3));
        try { await WarningCoreAsync("log-file-write-failed", "Log file output stopped: " + failure.Message, reporting.Token).ConfigureAwait(false); }
        catch (Exception secondary) when (secondary is IOException or UnauthorizedAccessException or OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_log is TextWriter writer)
        {
            _log = null;
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                await ReportLogFailureAsync(failure).ConfigureAwait(false);
            }
        }
        _writes.Dispose();
        _progress?.Dispose();
    }

    internal void Human(string text, string role, bool newline = true, TextWriter? writer = null)
    {
        writer ??= context.Output;
        bool color = UseColor(writer);
        Color tint = role switch { "error" => Color.Red, "warning" => Color.Yellow, "subject" => Color.Magenta1, "heading" => Color.Cyan1, "information" => Color.Cyan, _ => Color.Green };
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = color ? AnsiSupport.Yes : AnsiSupport.No, ColorSystem = ColorSystemSupport.Standard, Out = new AnsiConsoleOutput(writer) });
        string safe = string.Concat(text.Select(character => char.IsControl(character) && character is not '\n' and not '\t' ? $"\\u{(int)character:x4}" : character.ToString()));
        console.Write(new Text(safe + (newline ? "\n" : ""), new Style(tint, decoration: role is "heading" or "subject" ? Decoration.Bold : Decoration.None)));
    }

    private bool UseColor(TextWriter writer) => !Machine && string.IsNullOrEmpty(context.Environment.GetValueOrDefault("NO_COLOR")) && invocation.Text("color") != "never" && (invocation.Text("color") == "always" || !string.IsNullOrEmpty(context.Environment.GetValueOrDefault("FORCE_COLOR")) || (context.Environment.GetValueOrDefault("CLICOLOR_FORCE") is string force && force is not "" and not "0") || (context.Environment.GetValueOrDefault("CLICOLOR") != "0" && (ReferenceEquals(writer, context.Error) ? context.ErrorTerminal : context.Terminal)));

    private static void Json(TextWriter writer, object? value)
    {
        writer.WriteLine(JsonSerializer.Serialize(value));
        writer.Flush();
    }

    private static async ValueTask JsonAsync(TextWriter writer, object? value, CancellationToken token)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token).ConfigureAwait(false);
        await writer.FlushAsync(token).ConfigureAwait(false);
    }
}
