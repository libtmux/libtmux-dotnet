namespace LibTmux.McpSwap;

internal enum ConfigFormat
{
    Json,
    Jsonc,
    Toml,
}

internal enum ConfigDialect
{
    Standard,
    Claude,
    Opencode,
}

internal sealed record ClientInfo(
    string Name,
    string Binary,
    string ConfigPath,
    ConfigFormat Format,
    ConfigDialect Dialect,
    string Container);

internal sealed class ClientCatalog
{
    private static readonly string[] CanonicalNames =
    [
        "claude",
        "codex",
        "cursor",
        "gemini",
        "grok",
        "agy",
        "opencode",
        "pi",
    ];

    private readonly Dictionary<string, ClientInfo> byName;

    private ClientCatalog(IReadOnlyList<ClientInfo> clients)
    {
        Clients = clients;
        byName = clients.ToDictionary(client => client.Name, StringComparer.Ordinal);
    }

    internal IReadOnlyList<ClientInfo> Clients { get; }

    internal ClientInfo this[string name] => byName[name];

    internal static ClientCatalog Create(string home, string configHome)
    {
        string absoluteHome = Path.GetFullPath(home);
        string absoluteConfigHome = Path.GetFullPath(configHome);
        return new ClientCatalog(
        [
            new("claude", "claude", Path.Combine(absoluteHome, ".claude.json"), ConfigFormat.Json, ConfigDialect.Claude, "mcpServers"),
            new("codex", "codex", Path.Combine(absoluteHome, ".codex", "config.toml"), ConfigFormat.Toml, ConfigDialect.Standard, "mcp_servers"),
            new("cursor", "cursor-agent", Path.Combine(absoluteHome, ".cursor", "mcp.json"), ConfigFormat.Json, ConfigDialect.Standard, "mcpServers"),
            new("gemini", "gemini", Path.Combine(absoluteHome, ".gemini", "settings.json"), ConfigFormat.Json, ConfigDialect.Standard, "mcpServers"),
            new("grok", "grok", Path.Combine(absoluteHome, ".grok", "config.toml"), ConfigFormat.Toml, ConfigDialect.Standard, "mcp_servers"),
            new("agy", "agy", Path.Combine(absoluteHome, ".gemini", "config", "mcp_config.json"), ConfigFormat.Json, ConfigDialect.Standard, "mcpServers"),
            new("opencode", "opencode", Path.Combine(absoluteConfigHome, "opencode", "opencode.jsonc"), ConfigFormat.Jsonc, ConfigDialect.Opencode, "mcp"),
            new("pi", "pi", Path.Combine(absoluteHome, ".pi", "agent", "mcp.json"), ConfigFormat.Jsonc, ConfigDialect.Standard, "mcpServers"),
        ]);
    }

    internal IReadOnlyList<ClientInfo> Select(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return Clients;
        }

        HashSet<string> selected = new(StringComparer.Ordinal);
        foreach (string raw in names)
        {
            string canonical = raw == "antigravity" ? "agy" : raw;
            if (!byName.ContainsKey(canonical))
            {
                throw new ArgumentException(
                    $"unknown client {raw}; expected one of {string.Join(", ", CanonicalNames)}",
                    nameof(names));
            }

            selected.Add(canonical);
        }

        return Clients.Where(client => selected.Contains(client.Name)).ToArray();
    }
}
