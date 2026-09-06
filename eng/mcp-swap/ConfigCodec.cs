using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LibTmux.McpSwap;

internal static class ConfigCodec
{
    private const string OpencodeSchema = "https://opencode.ai/config.json";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    internal static ServerSpec? Read(
        ClientInfo client,
        byte[] bytes,
        string server,
        string repository,
        ConfigScope scope)
    {
        string text = Decode(bytes, client.Name);
        if (client.Format == ConfigFormat.Toml)
        {
            return TomlEditor.Read(text, client.Container, server);
        }

        JsonObject root = ParseJson(client, text);
        JsonObject? container = GetContainer(root, ContainerPath(client, repository, scope), create: false);
        if (container is null || !container.TryGetPropertyValue(server, out JsonNode? entry))
        {
            return null;
        }

        return ParseEntry(client, entry);
    }

    internal static IReadOnlyDictionary<string, ServerSpec> ReadAll(
        ClientInfo client,
        byte[] bytes,
        string repository)
    {
        string text = Decode(bytes, client.Name);
        if (client.Format == ConfigFormat.Toml)
        {
            return TomlEditor.ReadAll(text, client.Container);
        }

        JsonObject root = ParseJson(client, text);
        SortedDictionary<string, ServerSpec> servers = new(StringComparer.Ordinal);
        IEnumerable<IReadOnlyList<string>> paths = client.Name == "claude"
            ? [[client.Container], ["projects", Path.GetFullPath(repository), "mcpServers"]]
            : [[client.Container]];
        foreach (IReadOnlyList<string> path in paths)
        {
            JsonObject? container = GetContainer(root, path, create: false);
            if (container is null)
            {
                continue;
            }

            foreach ((string name, JsonNode? entry) in container)
            {
                servers[name] = ParseEntry(client, entry);
            }
        }

        return servers;
    }

    internal static ConfigEdit Set(
        ClientInfo client,
        byte[] original,
        string server,
        ServerSpec spec,
        string repository,
        ConfigScope scope,
        IReadOnlySet<string>? removeEnvironment = null)
    {
        string text = Decode(original, client.Name);
        ServerSpec? current = Read(client, original, server, repository, scope);
        ServerSpec merged = spec.MergeEnvironment(
            current?.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal));
        if (removeEnvironment is not null)
        {
            Dictionary<string, string> environment = merged.Environment
                .Where(pair => !removeEnvironment.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            merged = merged.WithEnvironment(environment);
        }

        if (merged.Equals(current))
        {
            return new(original.ToArray(), ConfigAction.Unchanged);
        }

        bool existed = current is not null;
        byte[] output;
        if (client.Format == ConfigFormat.Toml)
        {
            output = StrictUtf8.GetBytes(TomlEditor.Set(text, client.Container, server, merged));
        }
        else
        {
            JsonObject root = ParseJson(client, text);
            JsonObject container = GetContainer(
                root,
                ContainerPath(client, repository, scope),
                create: true)!;
            container[server] = RenderEntry(client, merged);
            output = RenderJson(client, text, root);
        }

        ServerSpec? written = Read(client, output, server, repository, scope);
        if (!merged.Equals(written))
        {
            throw new InvalidDataException($"{client.Name} rendered route is not exact");
        }

        return new(output, existed ? ConfigAction.Replaced : ConfigAction.Added);
    }

    internal static ConfigEdit Delete(
        ClientInfo client,
        byte[] original,
        string server,
        string repository,
        ConfigScope scope)
    {
        string text = Decode(original, client.Name);
        if (Read(client, original, server, repository, scope) is null)
        {
            return new(original.ToArray(), ConfigAction.Unchanged);
        }

        byte[] output;
        if (client.Format == ConfigFormat.Toml)
        {
            output = StrictUtf8.GetBytes(TomlEditor.Delete(text, client.Container, server));
        }
        else
        {
            JsonObject root = ParseJson(client, text);
            JsonObject? container = GetContainer(
                root,
                ContainerPath(client, repository, scope),
                create: false);
            _ = container?.Remove(server);
            output = RenderJson(client, text, root);
        }

        if (Read(client, output, server, repository, scope) is not null)
        {
            throw new InvalidDataException($"{client.Name} route remains after deletion");
        }

        return new(output, ConfigAction.Removed);
    }

    internal static string Decode(byte[] bytes, string subject)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException failure)
        {
            throw new InvalidDataException($"{subject} config is not valid UTF-8", failure);
        }
    }

    private static JsonObject ParseJson(ClientInfo client, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            JsonObject seeded = [];
            if (client.Dialect == ConfigDialect.Opencode)
            {
                seeded["$schema"] = OpencodeSchema;
            }

            return seeded;
        }

        try
        {
            JsonObject root = client.Format == ConfigFormat.Jsonc
                ? JsoncEditor.Parse(text)
                : JsonNode.Parse(text) as JsonObject
                    ?? throw new InvalidDataException($"{client.Name} config root must be an object");
            ValidateJsonTree(root);
            return root;
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            throw new InvalidDataException($"{client.Name} config is not valid JSON", failure);
        }
    }

    private static void ValidateJsonTree(JsonNode? node)
    {
        if (node is JsonObject objectNode)
        {
            foreach ((string _, JsonNode? value) in objectNode)
            {
                ValidateJsonTree(value);
            }
        }
        else if (node is JsonArray arrayNode)
        {
            foreach (JsonNode? value in arrayNode)
            {
                ValidateJsonTree(value);
            }
        }
    }

    private static byte[] RenderJson(ClientInfo client, string original, JsonObject root)
    {
        string rendered;
        if (client.Format == ConfigFormat.Jsonc)
        {
            rendered = JsoncEditor.Merge(original, root);
        }
        else
        {
            rendered = JsonSerializer.Serialize(root, JsonOptions);
            if (original.Length == 0 || original.EndsWith('\n'))
            {
                rendered += "\n";
            }
        }

        return StrictUtf8.GetBytes(rendered);
    }

    private static IReadOnlyList<string> ContainerPath(
        ClientInfo client,
        string repository,
        ConfigScope scope) =>
        client.Name == "claude" && scope == ConfigScope.Project
            ? ["projects", Path.GetFullPath(repository), "mcpServers"]
            : [client.Container];

    private static JsonObject? GetContainer(
        JsonObject root,
        IReadOnlyList<string> path,
        bool create)
    {
        JsonObject current = root;
        foreach (string part in path)
        {
            if (!current.TryGetPropertyValue(part, out JsonNode? node))
            {
                if (!create)
                {
                    return null;
                }

                JsonObject child = [];
                current[part] = child;
                current = child;
                continue;
            }

            current = node as JsonObject
                ?? throw new InvalidDataException($"{string.Join('.', path)} must be an object");
        }

        return current;
    }

    private static JsonObject RenderEntry(ClientInfo client, ServerSpec spec)
    {
        JsonObject entry = [];
        if (client.Dialect == ConfigDialect.Opencode)
        {
            entry["type"] = "local";
            JsonArray command = [spec.Command];
            foreach (string argument in spec.Arguments)
            {
                command.Add(argument);
            }

            entry["command"] = command;
            if (spec.Environment.Count > 0)
            {
                entry["environment"] = EnvironmentNode(spec.Environment);
            }

            return entry;
        }

        if (client.Dialect == ConfigDialect.Claude)
        {
            entry["type"] = "stdio";
        }

        entry["command"] = spec.Command;
        entry["args"] = new JsonArray(
            spec.Arguments.Select(argument => JsonValue.Create(argument)).ToArray());
        if (spec.Environment.Count > 0 || client.Dialect == ConfigDialect.Claude)
        {
            entry["env"] = EnvironmentNode(spec.Environment);
        }

        return entry;
    }

    private static JsonObject EnvironmentNode(IReadOnlyDictionary<string, string> environment)
    {
        JsonObject node = [];
        foreach ((string name, string value) in environment)
        {
            node[name] = value;
        }

        return node;
    }

    private static ServerSpec ParseEntry(ClientInfo client, JsonNode? raw)
    {
        JsonObject entry = raw as JsonObject
            ?? throw new InvalidDataException($"{client.Name} server entry must be an object");
        string command;
        List<string> arguments = [];
        string environmentName;
        if (client.Dialect == ConfigDialect.Opencode)
        {
            JsonArray commandLine = entry["command"] as JsonArray
                ?? throw new InvalidDataException("opencode server command must be a string array");
            if (commandLine.Count == 0)
            {
                throw new InvalidDataException("opencode server command must not be empty");
            }

            command = StringValue(commandLine[0], "opencode command");
            arguments.AddRange(commandLine.Skip(1).Select(value => StringValue(value, "opencode argument")));
            environmentName = "environment";
        }
        else
        {
            command = StringValue(entry["command"], $"{client.Name} command");
            if (entry.TryGetPropertyValue("args", out JsonNode? rawArguments))
            {
                JsonArray array = rawArguments as JsonArray
                    ?? throw new InvalidDataException($"{client.Name} args must be an array");
                arguments.AddRange(array.Select(value => StringValue(value, $"{client.Name} argument")));
            }

            environmentName = "env";
        }

        SortedDictionary<string, string> environment = new(StringComparer.Ordinal);
        if (entry.TryGetPropertyValue(environmentName, out JsonNode? rawEnvironment))
        {
            JsonObject values = rawEnvironment as JsonObject
                ?? throw new InvalidDataException($"{client.Name} environment must be an object");
            foreach ((string name, JsonNode? value) in values)
            {
                environment[name] = StringValue(value, $"{client.Name} environment value");
            }
        }

        return new(command, arguments, environment);
    }

    private static string StringValue(JsonNode? node, string subject)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text) && text is not null)
        {
            return text;
        }

        throw new InvalidDataException($"{subject} must be a string");
    }
}
