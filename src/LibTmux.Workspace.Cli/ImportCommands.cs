using System.Text.Json.Nodes;

namespace LibTmux.Workspace.Cli;

internal static class ImportCommands
{
    internal static JsonObject Convert(JsonObject source, bool teamocil)
    {
        source = (JsonObject)source.DeepClone();
        if (teamocil && source["session"] is JsonObject session) source = session;
        JsonObject result = new()
        {
            ["session_name"] = (source[teamocil ? "name" : "project_name"] ?? source["name"])?.DeepClone(),
            ["windows"] = new JsonArray(),
        };
        Copy(source, result, teamocil ? "root" : source.ContainsKey("project_root") ? "project_root" : "root", "start_directory");
        if (!teamocil)
        {
            Copy(source, result, "socket_name", "socket_name");
            if ((source["cli_args"] ?? source["tmux_options"]) is JsonNode config) result["config"] = config.ToString().Replace("-f", "", StringComparison.Ordinal).Trim();
            if (source["pre"] is JsonNode pre)
            {
                if (source["pre_window"] is JsonNode preWindow) { result["shell_command"] = pre.DeepClone(); result["shell_command_before"] = preWindow.DeepClone(); }
                else result["shell_command_before"] = pre is JsonArray ? pre.DeepClone() : new JsonArray(pre.DeepClone());
            }
            if (source["rbenv"] is JsonNode ruby)
            {
                JsonArray before = result["shell_command_before"] as JsonArray ?? new JsonArray(result["shell_command_before"]?.DeepClone());
                before.Add("rbenv shell " + ruby);
                result["shell_command_before"] = before;
            }
        }
        JsonArray windows = (source["tabs"] ?? source["windows"]) as JsonArray ?? throw new CliException("invalid-config", "Importer expects a windows list.");
        foreach (JsonObject item in windows.OfType<JsonObject>())
        {
            if (teamocil)
            {
                JsonObject window = new() { ["window_name"] = item["name"]?.DeepClone() };
                foreach (string key in new[] { "layout", "clear" }) Copy(item, window, key, key);
                Copy(item, window, "root", "start_directory");
                if (item["filters"] is JsonObject filters)
                {
                    Copy(filters, window, "before", "shell_command_before");
                    Copy(filters, window, "after", "shell_command_after");
                }
                if ((item["splits"] ?? item["panes"]) is JsonArray panes)
                {
                    JsonArray converted = [];
                    foreach (JsonNode? raw in panes)
                    {
                        JsonNode? pane = raw?.DeepClone();
                        if (pane is JsonObject map)
                        {
                            if (map.Remove("cmd", out JsonNode? command)) map["shell_command"] = command;
                            map.Remove("width");
                        }
                        converted.Add(pane);
                    }
                    window["panes"] = converted;
                }
                result["windows"]!.AsArray().Add(window);
            }
            else
            {
                foreach ((string name, JsonNode? value) in item)
                {
                    JsonObject window = new() { ["window_name"] = name };
                    if (value is JsonObject settings)
                    {
                        Copy(settings, window, "pre", "shell_command_before");
                        Copy(settings, window, "root", "start_directory");
                        Copy(settings, window, "panes", "panes");
                        Copy(settings, window, "layout", "layout");
                    }
                    else window["panes"] = value is JsonArray ? value.DeepClone() : new JsonArray(value?.DeepClone());
                    result["windows"]!.AsArray().Add(window);
                }
            }
        }
        return result;
    }

    private static void Copy(JsonObject source, JsonObject destination, string key, string target)
    {
        if (source.TryGetPropertyValue(key, out JsonNode? value)) destination[target] = value?.DeepClone();
    }
}
