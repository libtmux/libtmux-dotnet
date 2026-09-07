using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace LibTmux.Mcp;

internal sealed record McpRuntimeDisclosure(
    string SocketSelector,
    string SocketProvenance,
    string ConfigurationProvenance,
    string ServerState,
    string ResolvedSocketPath,
    string AttachCommand,
    bool TeardownExplicitlySelected)
{
    internal static McpRuntimeDisclosure Unknown(ServerConnectionOptions options)
    {
        string resolvedPath = options.SocketPath is string path ? Path.GetFullPath(path) : "";
        return new(
            resolvedPath.Length == 0
                ? $"name:{options.SocketName ?? "default"}"
                : $"path:{resolvedPath}",
            "unknown",
            "unknown",
            ServerState: "unknown",
            resolvedPath,
            BuildAttachCommand(options, resolvedPath),
            TeardownExplicitlySelected: false);
    }

    internal static string BuildAttachCommand(
        ServerConnectionOptions options,
        string resolvedSocketPath)
    {
        string selector = resolvedSocketPath.Length == 0
            ? $"-L {ShellQuote(options.SocketName ?? "default")}"
            : $"-S {ShellQuote(resolvedSocketPath)}";
        return $"{ShellQuote(options.TmuxBinaryPath)} -N {selector} attach";
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

}

[McpServerResourceType]
[UnsupportedOSPlatform("windows")]
internal sealed class CapabilityResource
{
    private readonly CapabilityRegistry _registry;
    private readonly McpRuntimeDisclosure _runtime;

    public CapabilityResource(CapabilityRegistry registry, McpRuntimeDisclosure runtime)
    {
        _registry = registry;
        _runtime = runtime;
    }

    [McpServerResource(
        UriTemplate = "tmux://capabilities",
        Name = "tmux_capabilities",
        Title = "Effective tmux capabilities",
        MimeType = "application/json")]
    [Description("The startup-frozen tmux connection and effective MCP tool capabilities.")]
    public string Read()
    {
        var rows = new JsonArray();
        foreach (ToolDefinition definition in _registry.Definitions)
        {
            rows.Add(definition.CapabilityRow());
        }

        var payload = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["frozen"] = true,
            ["socket"] = new JsonObject
            {
                ["selector"] = _runtime.SocketSelector,
                ["selectionProvenance"] = _runtime.SocketProvenance,
                ["serverState"] = _runtime.ServerState,
                ["configurationProvenance"] = _runtime.ConfigurationProvenance,
                ["namespaceBoundary"] = "tmux-objects-only",
            },
            ["boundary"] = new JsonObject
            {
                ["oneSocketPerProcess"] = true,
                ["perCallSocketSelection"] = false,
                ["hostCommandExecution"] = false,
                ["dynamicResources"] = false,
            },
            ["connection"] = new JsonObject
            {
                ["socketSelector"] = _runtime.SocketSelector,
                ["socketProvenance"] = _runtime.SocketProvenance,
                ["resolvedSocketPath"] = _runtime.ResolvedSocketPath,
                ["serverState"] = _runtime.ServerState,
                ["configurationProvenance"] = _runtime.ConfigurationProvenance,
                ["attachCommand"] = _runtime.AttachCommand,
            },
            ["toolsets"] = JsonSerializer.SerializeToNode(
                Enum.GetValues<Toolset>()
                    .Where(_registry.Selection.Toolsets.Contains)
                    .Select(value => value.ToString().ToLowerInvariant())),
            ["includedTools"] = JsonSerializer.SerializeToNode(
                _registry.Selection.IncludedNames.Order(StringComparer.Ordinal)),
            ["excludedTools"] = JsonSerializer.SerializeToNode(
                _registry.Selection.ExcludedNames.Order(StringComparer.Ordinal)),
            ["toolCount"] = _registry.Definitions.Length,
            ["effectiveTools"] = JsonSerializer.SerializeToNode(
                _registry.Definitions.Select(definition => definition.Name)),
            ["hostCommandTools"] = 0,
            ["toolFilteringBoundary"] = "interface-shaping-not-authorization",
            ["executionAuthority"] = "tmux-user",
            ["operatingSystemBoundary"] = "none",
            ["tools"] = rows,
        };
        return JsonSerializer.Serialize(payload, ToolJson.Options);
    }
}
