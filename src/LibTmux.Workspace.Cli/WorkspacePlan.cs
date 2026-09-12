using System.Globalization;
using System.Text.Json.Nodes;

namespace LibTmux.Workspace.Cli;

internal sealed record CommandPlan(string Text, bool Enter, double Before, double After);
internal sealed record PanePlan(string? Directory, string? Shell, bool Focus, Dictionary<string, string> Environment, CommandPlan[] Commands);
internal sealed record WindowPlan(string? Name, int? Index, string? Layout, bool Focus, Dictionary<string, string> Options, Dictionary<string, string> OptionsAfter, PanePlan[] Panes);
internal sealed record WorkspacePlan(string Name, string Source, string Directory, string? BeforeScript, string Readiness, Dictionary<string, string> Options, Dictionary<string, string> GlobalOptions, Dictionary<string, string> Environment, WindowPlan[] Windows)
{
    private static readonly string[] RootKeys = ["session_name", "start_directory", "windows", "options", "global_options", "environment", "before_script", "shell_command_before", "suppress_history", "workspace_builder_options", "plugins", "workspace_builder", "workspace_builder_paths", "config", "socket_name"];
    private static readonly string[] WindowKeys = ["window_name", "window_index", "window_shell", "start_directory", "panes", "layout", "focus", "options", "options_after", "environment", "shell_command_before", "suppress_history"];
    private static readonly string[] PaneKeys = ["shell_command", "shell_command_before", "start_directory", "shell", "focus", "environment", "enter", "sleep_before", "sleep_after", "suppress_history"];
    private static readonly string[] CommandKeys = ["cmd", "enter", "sleep_before", "sleep_after"];
    private static readonly string[] BuilderKeys = ["pane_readiness"];

    internal static WorkspacePlan Parse(JsonObject document, string source, DocumentStore store, string? overrideName = null)
    {
        Keys(document, RootKeys, "workspace");
        string name = overrideName ?? Text(document, "session_name") ?? throw Invalid("session_name is required.");
        name = store.Expand(name);
        if (string.IsNullOrWhiteSpace(name) || name.Any(character => char.IsControl(character) || character is ':' or '.')) throw Invalid("session_name contains a character tmux cannot preserve.");
        string fileDirectory = Path.GetDirectoryName(source)!;
        string directory = ResolveDirectory(document, fileDirectory, store);
        JsonArray windows = document["windows"] as JsonArray ?? throw Invalid("windows must be a list.");
        if (windows.Count == 0) throw Invalid("At least one window is required.");
        JsonObject builder = document["workspace_builder_options"] as JsonObject ?? (document["workspace_builder_options"] is null ? [] : throw Invalid("workspace_builder_options must be a mapping."));
        Keys(builder, BuilderKeys, "workspace_builder_options");
        string readiness = (Text(builder, "pane_readiness") ?? "auto").ToLowerInvariant() switch
        {
            "auto" => "auto", "always" or "true" or "on" or "yes" or "1" => "always", "never" or "false" or "off" or "no" or "0" => "never",
            _ => throw Invalid("pane_readiness must be auto, always or never."),
        };
        List<WindowPlan> plans = [];
        HashSet<int> indexes = [];
        foreach (JsonNode? item in windows)
        {
            JsonObject window = item as JsonObject ?? throw Invalid("Each window must be a mapping.");
            Keys(window, WindowKeys, "window");
            string windowDirectory = ResolveDirectory(window, directory, store);
            int? index = window["window_index"] is null ? null : int.TryParse(window["window_index"]!.ToString(), CultureInfo.InvariantCulture, out int number) && number >= 0 ? number : throw Invalid("window_index must be a nonnegative integer.");
            if (index is int value && !indexes.Add(value)) throw Invalid($"Duplicate window_index {value}.");
            JsonArray panes = window["panes"] as JsonArray ?? (window.ContainsKey("panes") ? throw Invalid("panes must be a list.") : new JsonArray((JsonNode?)null));
            if (panes.Count == 0) panes = new JsonArray((JsonNode?)null);
            List<PanePlan> panePlans = [];
            foreach (JsonNode? raw in panes)
            {
                JsonObject pane = raw as JsonObject ?? new JsonObject { ["shell_command"] = raw?.DeepClone() };
                Keys(pane, PaneKeys, "pane");
                bool enter = Boolean(pane, "enter", true);
                bool suppress = Boolean(pane, "suppress_history", Boolean(window, "suppress_history", Boolean(document, "suppress_history", true)));
                double before = Seconds(pane, "sleep_before", 0);
                double after = Seconds(pane, "sleep_after", 0);
                CommandSettings state = new(enter, before, after);
                CommandPlan[] commands = new[] { document["shell_command_before"], window["shell_command_before"], pane["shell_command_before"], pane["shell_command"] }.SelectMany(command => Commands(command, state, suppress, store)).ToArray();
                string? shell = Text(pane, "shell") ?? Text(window, "window_shell");
                panePlans.Add(new PanePlan(ResolveDirectory(pane, windowDirectory, store), shell is null ? null : store.Expand(shell), Boolean(pane, "focus", false), Mapping(pane["environment"] ?? window["environment"], store), commands));
            }
            plans.Add(new WindowPlan(Text(window, "window_name") is string windowName ? store.Expand(windowName) : null, index, Text(window, "layout"), Boolean(window, "focus", false), Mapping(window["options"], store), Mapping(window["options_after"], store), panePlans.ToArray()));
        }
        return new WorkspacePlan(name, source, directory, Text(document, "before_script") is string script ? store.Expand(script) : null, readiness, Mapping(document["options"], store), Mapping(document["global_options"], store), Mapping(document["environment"], store), plans.ToArray());
    }

    private sealed class CommandSettings(bool enter, double before, double after)
    {
        internal bool Enter { get; set; } = enter;
        internal double Before { get; set; } = before;
        internal double After { get; set; } = after;
    }

    private static IEnumerable<CommandPlan> Commands(JsonNode? node, CommandSettings state, bool suppress, DocumentStore store)
    {
        if (node is null) yield break;
        if (node is JsonArray list)
        {
            foreach (JsonNode? command in list) foreach (CommandPlan plan in Commands(command, state, suppress, store)) yield return plan;
            yield break;
        }
        string commandText;
        if (node is JsonObject action)
        {
            Keys(action, CommandKeys, "command");
            commandText = Text(action, "cmd") ?? throw Invalid("Command mappings require cmd.");
            state.Enter = Boolean(action, "enter", state.Enter);
            state.Before = Seconds(action, "sleep_before", state.Before);
            state.After = Seconds(action, "sleep_after", state.After);
        }
        else commandText = node.ToString();
        if (commandText is "" or "blank" or "pane") yield break;
        if (commandText.Contains('\0')) throw Invalid("Commands cannot contain NUL.");
        yield return new CommandPlan((suppress ? " " : "") + store.Expand(commandText), state.Enter, state.Before, state.After);
    }

    internal static Dictionary<string, string> Mapping(JsonNode? value, DocumentStore store)
    {
        if (value is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        if (value is not JsonObject map) throw Invalid("Options and environments must be mappings.");
        return map.ToDictionary(pair => pair.Key, pair => pair.Value is JsonValue scalar ? store.Expand(scalar.ToString()) : throw Invalid($"'{pair.Key}' must have a scalar value."), StringComparer.Ordinal);
    }

    internal static string? Text(JsonObject node, string key) => node[key] is null ? null : node[key] is JsonValue value ? value.ToString() : throw Invalid($"{key} must be a scalar.");

    private static string ResolveDirectory(JsonObject node, string inherited, DocumentStore store)
    {
        string? value = Text(node, "start_directory");
        return value is null ? inherited : Path.GetFullPath(store.Expand(value), inherited);
    }

    private static bool Boolean(JsonObject node, string key, bool fallback) => node[key] is null ? fallback : node[key]!.ToString().ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => throw Invalid($"{key} must be a boolean."),
    };

    private static double Seconds(JsonObject node, string key, double fallback) => node[key] is null ? fallback : double.TryParse(node[key]!.ToString(), CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value >= 0 ? value : throw Invalid($"{key} must be a nonnegative number.");

    private static void Keys(JsonObject node, string[] allowed, string scope)
    {
        foreach (string key in node.Select(pair => pair.Key)) if (!allowed.Contains(key, StringComparer.Ordinal)) throw Invalid($"Unsupported {scope} key '{key}'.");
    }

    private static CliException Invalid(string message) => new("invalid-config", message);
}
