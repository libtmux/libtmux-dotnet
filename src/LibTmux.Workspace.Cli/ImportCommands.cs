using System.Text.Json.Nodes;

namespace LibTmux.Workspace.Cli;

internal static class ImportCommands
{
    internal static JsonObject Convert(JsonObject source, bool teamocil)
    {
        if (teamocil && source["session"] is JsonNode session)
        {
            Keys(source, "document", "session");
            source = session as JsonObject ?? throw Invalid("Teamocil session must be a mapping.");
        }
        Keys(source, "project", teamocil
            ? ["name", "root", "windows", "tabs"]
            : ["name", "project_name", "root", "project_root", "windows", "tabs", "socket_name", "cli_args", "tmux_options", "pre", "pre_window", "pre_tab", "rbenv", "rvm"]);
        JsonObject result = new()
        {
            ["session_name"] = String(Alias(source, "name", "project_name"), "name"),
            ["start_directory"] = String(Alias(source, "root", "project_root"), "root"),
        };
        if (!teamocil)
        {
            if (Commands(source["pre"], "pre").Any(command => !string.IsNullOrWhiteSpace(command!.GetValue<string>()))) throw Invalid("Project pre runs in the launcher shell and cannot be imported as pane commands.");
            result["socket_name"] = String(source["socket_name"], "socket_name");
            if (String(Alias(source, "cli_args", "tmux_options"), "tmux_options") is string arguments)
            {
                string[] parsed = ProcessCommands.SplitArguments(arguments);
                if (parsed.Length != 2 || parsed[0] != "-f") throw Invalid("Importer tmux_options must contain only '-f PATH'.");
                result["config"] = parsed[1];
            }
            JsonNode? before = Alias(source, "pre_window", "pre_tab");
            JsonNode? ruby = Alias(source, "rbenv", "rvm");
            if (ruby is not null)
            {
                if (Commands(before, "pre_window").Count != 0) throw Invalid("Specify either a Ruby version selector or pre_window, not both.");
                before = (source["rbenv"] is not null ? "rbenv shell " : "rvm use ") + String(ruby, "Ruby version");
            }
            result["shell_command_before"] = Joined(before, "pre_window", "; ");
        }
        JsonArray windows = Alias(source, "windows", "tabs") as JsonArray ?? throw Invalid("Importer expects a windows list.");
        JsonArray converted = [];
        bool focused = false;
        foreach (JsonNode? item in windows)
        {
            JsonObject window = item as JsonObject ?? throw Invalid("Each imported window must be a mapping.");
            converted.Add(teamocil ? TeamocilWindow(window, ref focused) : TmuxinatorWindow(window));
        }
        result["windows"] = converted;
        return result;
    }

    private static JsonObject TeamocilWindow(JsonObject source, ref bool focused)
    {
        Keys(source, "window", "name", "root", "layout", "panes", "splits", "options", "focus");
        JsonObject window = new()
        {
            ["window_name"] = String(source["name"], "window.name"),
            ["start_directory"] = String(source["root"], "window.root"),
            ["layout"] = String(source["layout"], "window.layout"),
            ["options"] = source["options"]?.DeepClone(),
            ["focus"] = FirstFocus(source, ref focused),
        };
        JsonNode? raw = Alias(source, "panes", "splits");
        if (raw is not null && raw is not JsonArray) throw Invalid("Window panes must be a list.");
        JsonArray panes = [];
        bool paneFocused = false;
        foreach (JsonNode? pane in raw as JsonArray ?? [])
        {
            if (pane is JsonObject map)
            {
                Keys(map, "pane", "commands", "cmd", "focus");
                panes.Add(new JsonObject
                {
                    ["shell_command"] = Joined(Alias(map, "commands", "cmd"), "pane.commands", "; "),
                    ["focus"] = FirstFocus(map, ref paneFocused),
                });
            }
            else panes.Add(new JsonObject { ["shell_command"] = Joined(pane, "pane.commands", "; ") });
        }
        window["panes"] = panes;
        return window;
    }

    private static JsonObject TmuxinatorWindow(JsonObject source)
    {
        if (source.Count != 1) throw Invalid("Each tmuxinator window must contain exactly one name.");
        (string name, JsonNode? content) = source.Single();
        JsonObject window = new() { ["window_name"] = name };
        JsonArray panes = [];
        if (content is JsonObject settings)
        {
            Keys(settings, "window", "root", "layout", "panes", "pre", "synchronize");
            window["start_directory"] = String(settings["root"], "window.root");
            window["layout"] = String(settings["layout"], "window.layout");
            if (settings["panes"] is not null && settings["panes"] is not JsonArray) throw Invalid("Window panes must be a list.");
            foreach (JsonNode? pane in settings["panes"] as JsonArray ?? [])
            {
                if (pane is JsonObject) throw Invalid("Named tmuxinator pane titles cannot be preserved by this importer.");
                panes.Add(new JsonObject { ["shell_command"] = Commands(pane, "pane commands") });
            }
            string? pre = Joined(settings["pre"], "window.pre", " && ");
            if (!string.IsNullOrEmpty(pre) && panes.Count == 0) throw Invalid("Window pre requires explicit panes; tmuxinator does not run it without them.");
            window["shell_command_before"] = pre;
            if (settings["synchronize"] is JsonNode synchronize && synchronize.ToString() is not ("false" or "off" or "0"))
            {
                string option = synchronize.ToString() switch
                {
                    "true" or "before" => "options",
                    "after" => "options_after",
                    _ => throw Invalid("Window synchronize must be false, true, 'before' or 'after'."),
                };
                window[option] = new JsonObject { ["synchronize-panes"] = true };
            }
        }
        else panes.Add(new JsonObject { ["shell_command"] = Commands(content, "window commands") });
        window["panes"] = panes;
        return window;
    }

    private static JsonArray Commands(JsonNode? node, string path)
    {
        JsonArray result = [];
        foreach (JsonNode? command in node is JsonArray array ? array : new JsonArray(node?.DeepClone()))
        {
            if (command is null) continue;
            result.Add(JsonValue.Create(String(command, path)));
        }
        return result;
    }

    private static string? Joined(JsonNode? node, string path, string separator)
    {
        JsonArray commands = Commands(node, path);
        return commands.Count == 0 ? null : string.Join(separator, commands.Select(command => command!.GetValue<string>()));
    }

    private static bool FirstFocus(JsonObject source, ref bool focused)
    {
        bool requested = source["focus"] is null ? false
            : source["focus"] is JsonValue value && value.TryGetValue(out bool flag) ? flag
            : throw Invalid("Focus must be a boolean.");
        bool selected = requested && !focused;
        focused |= requested;
        return selected;
    }

    private static JsonNode? Alias(JsonObject source, params string[] names)
    {
        JsonNode? selected = null;
        foreach (string name in names)
        {
            if (source[name] is not JsonNode value) continue;
            if (selected is not null && !JsonNode.DeepEquals(selected, value)) throw Invalid($"Conflicting importer fields '{string.Join("', '", names)}'.");
            selected = value;
        }
        return selected;
    }

    private static string? String(JsonNode? node, string path) => node is null ? null
        : node is JsonValue value && value.TryGetValue(out string? text) ? text
        : throw Invalid($"Imported {path} must contain strings.");

    private static void Keys(JsonObject source, string scope, params string[] allowed)
    {
        foreach ((string key, JsonNode? value) in source)
            if (value is not null && !allowed.Contains(key, StringComparer.Ordinal)) throw Unsupported($"Unsupported imported {scope} field '{key}'.");
    }

    private static CliException Invalid(string message) => new("invalid_workspace", message);

    private static CliException Unsupported(string message) => new("unsupported_key", message);
}
