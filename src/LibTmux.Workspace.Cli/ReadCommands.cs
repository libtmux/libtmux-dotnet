using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LibTmux.Workspace.Cli;

internal sealed class ReadCommands(CliContext context, Invocation invocation, Output output)
{
    private static readonly string[] SearchFields = ["name", "n", "session", "session_name", "s", "path", "p", "window", "w", "pane"];
    private readonly DocumentStore _documents = new(context);

    internal void List()
    {
        JsonArray directories = new(_documents.Candidates().Select(path => (JsonNode)new JsonObject { ["path"] = path, ["exists"] = Directory.Exists(path), ["active"] = path == _documents.GlobalDirectory }).ToArray());
        output.Records(_documents.Discover().Select(item => Describe(item.Path, item.Source)), "workspaces", directories);
    }

    internal void Search()
    {
        string[] terms = invocation.Many("patterns");
        if (terms.Length == 0) throw new CliException("missing-pattern", "At least one search pattern is required.", 2);
        string[] selected = invocation.Many("fields");
        if (selected.Length == 0) selected = ["name", "session_name", "path", "window", "pane"];
        selected = selected.Select(Field).Distinct(StringComparer.Ordinal).ToArray();
        var expressions = terms.Select(term =>
        {
            string[] parts = term.Split(':', 2);
            bool prefix = parts.Length == 2 && SearchFields.Contains(parts[0], StringComparer.OrdinalIgnoreCase);
            string pattern = prefix ? parts[1] : term;
            if (invocation.Flag("fixed")) pattern = Regex.Escape(pattern);
            if (invocation.Flag("word")) pattern = "\\b" + pattern + "\\b";
            RegexOptions flags = RegexOptions.CultureInvariant;
            if (invocation.Flag("ignore_case") || (invocation.Flag("smart_case") && !(prefix ? parts[1] : term).Any(char.IsUpper))) flags |= RegexOptions.IgnoreCase;
            try { return (Fields: prefix ? new[] { Field(parts[0]) } : selected, Regex: new Regex(pattern, flags, TimeSpan.FromSeconds(1))); }
            catch (ArgumentException error) { throw new CliException("invalid-pattern", error.Message, 2); }
        }).ToArray();
        List<JsonObject> matches = [];
        foreach ((string path, string source) in _documents.Discover())
        {
            JsonObject document;
            try { document = DocumentStore.Read(path); }
            catch (Exception error) when (error is CliException or IOException) { document = []; }
            Dictionary<string, string[]> fields = Fields(document, Private(path));
            bool[] found = expressions.Select(expression => expression.Fields.Any(field => fields[field].Any(value => expression.Regex.IsMatch(value)))).ToArray();
            bool match = invocation.Flag("any") ? found.Any(value => value) : found.All(value => value);
            if (invocation.Flag("invert")) match = !match;
            if (!match) continue;
            JsonObject record = new() { ["name"] = fields["name"][0], ["path"] = fields["path"][0], ["session_name"] = fields["session_name"][0], ["source"] = source };
            JsonObject details = [];
            foreach (var expression in expressions)
            {
                foreach (string field in expression.Fields)
                {
                    foreach (string value in fields[field])
                    {
                        Match occurrence = expression.Regex.Match(value);
                        if (occurrence.Success)
                        {
                            if (details[field] is not JsonArray) details[field] = new JsonArray();
                            details[field]!.AsArray().Add(occurrence.Value);
                        }
                    }
                }
            }
            record["matched_fields"] = new JsonArray(details.Select(pair => JsonValue.Create(pair.Key)).ToArray());
            record["matches"] = details;
            matches.Add(record);
        }
        output.Records(matches);
    }

    internal void Convert()
    {
        string path = _documents.Resolve(invocation.Many("files")[0]);
        JsonObject document = DocumentStore.Read(path);
        string format = invocation.Text("workspace_format") ?? (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? "yaml" : "json");
        SaveOrPrint(document, format, Path.ChangeExtension(path, "." + format));
    }

    internal void Import(bool teamocil)
    {
        string directory = teamocil ? Path.Combine(context.Home, ".teamocil") : _documents.Expand(context.Environment.GetValueOrDefault("TMUXINATOR_CONFIG") ?? Path.Combine(context.Home, ".tmuxinator"));
        string path = _documents.Resolve(invocation.Many("files")[0], directory);
        JsonObject document = ImportCommands.Convert(DocumentStore.Read(path), teamocil);
        string format = invocation.Text("workspace_format") ?? "yaml";
        SaveOrPrint(document, format, Path.Combine(context.Directory, Path.GetFileNameWithoutExtension(path) + "." + format));
    }

    internal void SaveOrPrint(JsonObject document, string format, string? suggested = null)
    {
        string? destination = invocation.Text("save_to");
        if (destination is null && invocation.Machine)
        {
            if (invocation.Flag("ndjson") && invocation.Command == "freeze") output.Result(new { schema_version = 1, command = invocation.Command, status = "ok", workspace = document, warnings = CaptureWarnings });
            else if (invocation.Flag("ndjson")) output.Result(new { schema_version = 1, command = invocation.Command, status = "ok", document });
            else output.Result(document);
            return;
        }
        destination ??= suggested;
        if (destination is null) destination = Prompt("Save to: ");
        if (!Path.IsPathRooted(destination)) destination = Path.Combine(context.Directory, destination);
        if (!invocation.Flag("yes") && !invocation.Machine && !Confirm($"Save workspace to '{destination}'?")) return;
        DocumentStore.Save(destination, document, format, invocation.Flag("force"));
        if (invocation.Machine || !invocation.Flag("quiet")) output.Result(new { schema_version = 1, command = invocation.Command, status = "ok", destination, format, warnings = invocation.Command == "freeze" ? CaptureWarnings : [] }, $"Saved {destination}");
    }

    private static readonly string[] CaptureWarnings = ["Capture preserves current topology, layout, directory and focus. Shell command history, startup hooks, environment provenance and plugin state cannot be recovered."];

    internal bool Confirm(string message)
    {
        if (invocation.Machine) throw new CliException("confirmation-required", message);
        string answer = Prompt(message + " [y/N] ");
        return answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    internal string Prompt(string message)
    {
        if (!context.Terminal) throw new CliException("input-required", message + " Supply explicit arguments for noninteractive use.");
        context.Error.Write(message);
        return Console.ReadLine() ?? throw new CliException("input-closed", "Input closed before a response was received.");
    }

    private JsonObject Describe(string path, string source)
    {
        FileInfo file = new(path);
        JsonObject record = new() { ["name"] = Path.GetFileNameWithoutExtension(path), ["path"] = Private(path), ["source"] = source, ["format"] = file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml", ["size"] = file.Length, ["mtime"] = file.LastWriteTimeUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ["session_name"] = null };
        try
        {
            JsonObject document = DocumentStore.Read(path);
            record["session_name"] = document["session_name"]?.DeepClone();
            if (invocation.Flag("full")) record["config"] = document;
        }
        catch (Exception error) when (error is CliException or IOException)
        {
            record["error"] = error.Message;
        }
        return record;
    }

    private string Private(string path) => path == context.Home ? "~" : path.StartsWith(context.Home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + path[context.Home.Length..] : path;

    private static string Field(string name) => name.ToLowerInvariant() switch
    {
        "s" or "session" or "session_name" => "session_name", "p" => "path", "w" => "window", "n" => "name",
        "name" or "path" or "window" or "pane" => name.ToLowerInvariant(),
        _ => throw new CliException("invalid-field", $"Unknown search field '{name}'.", 2),
    };

    private static Dictionary<string, string[]> Fields(JsonObject document, string path)
    {
        JsonObject[] windows = (document["windows"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        return new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["name"] = [Path.GetFileNameWithoutExtension(path)],
            ["session_name"] = [document["session_name"]?.ToString() ?? ""],
            ["path"] = [path],
            ["window"] = windows.Select(window => window["window_name"]?.ToString() ?? "").ToArray(),
            ["pane"] = windows.SelectMany(window => (window["panes"] as JsonArray)?.SelectMany(PaneCommands) ?? []).ToArray(),
        };
    }

    private static IEnumerable<string> PaneCommands(JsonNode? pane)
    {
        if (pane is JsonValue) yield return pane.ToString();
        else if (pane is JsonObject map)
        {
            if (map["shell_command"] is JsonValue command) yield return command.ToString();
            else if (map["shell_command"] is JsonArray commands) foreach (JsonNode? item in commands) if (item is not null) yield return item.ToString();
        }
    }
}
