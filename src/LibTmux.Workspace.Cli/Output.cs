using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace LibTmux.Workspace.Cli;

internal sealed record CliContext(TextWriter Output, TextWriter Error, string Directory, IReadOnlyDictionary<string, string?> Environment, CancellationToken CancellationToken)
{
    internal string Home => Environment.GetValueOrDefault("HOME") ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
    internal bool Terminal => ReferenceEquals(Output, Console.Out) && !Console.IsOutputRedirected;
}

internal sealed class CliException(string code, string message, int exitCode = 1) : Exception(message)
{
    internal string Code { get; } = code;
    internal int ExitCode { get; } = exitCode;
}

internal sealed class Output(CliContext context, Invocation invocation)
{
    private int _sequence;
    internal bool Machine => invocation.Machine;

    internal void Result(object? value, string? status = null)
    {
        if (Machine) Json(context.Output, value);
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

    internal void Event(string name, object? data = null)
    {
        if (invocation.Flag("ndjson")) Json(context.Output, new { schema_version = 1, command = invocation.Command, @event = name, sequence = ++_sequence, data });
    }

    internal void Diagnostic(string code, string message)
    {
        if (Machine) Json(context.Error, new { code, message });
        else Human(message, "error", writer: context.Error);
    }

    internal void Warning(string code, string message)
    {
        if (Machine) Json(context.Error, new { code, message, severity = "warning" });
        else Human(message, "warning", writer: context.Error);
    }

    internal void Human(string text, string role, bool newline = true, TextWriter? writer = null)
    {
        writer ??= context.Output;
        bool color = !Machine && string.IsNullOrEmpty(context.Environment.GetValueOrDefault("NO_COLOR")) && invocation.Text("color") != "never" && (invocation.Text("color") == "always" || !string.IsNullOrEmpty(context.Environment.GetValueOrDefault("FORCE_COLOR")) || (context.Environment.GetValueOrDefault("CLICOLOR_FORCE") is string force && force is not "" and not "0") || (context.Environment.GetValueOrDefault("CLICOLOR") != "0" && context.Terminal));
        Color tint = role switch { "error" => Color.Red, "warning" => Color.Yellow, "subject" => Color.Magenta1, "heading" => Color.Cyan1, "information" => Color.Cyan, _ => Color.Green };
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = color ? AnsiSupport.Yes : AnsiSupport.No, ColorSystem = ColorSystemSupport.Standard, Out = new AnsiConsoleOutput(writer) });
        string safe = string.Concat(text.Select(character => char.IsControl(character) && character is not '\n' and not '\t' ? $"\\u{(int)character:x4}" : character.ToString()));
        console.Write(new Text(safe + (newline ? "\n" : ""), new Style(tint, decoration: role is "heading" or "subject" ? Decoration.Bold : Decoration.None)));
    }

    private static void Json(TextWriter writer, object? value)
    {
        writer.WriteLine(JsonSerializer.Serialize(value));
        writer.Flush();
    }
}
