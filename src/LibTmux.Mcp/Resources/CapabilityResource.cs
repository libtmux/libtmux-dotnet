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
    bool TeardownExplicitlySelected)
{
    internal static McpRuntimeDisclosure Unknown(ServerConnectionOptions options) => new(
        options.SocketPath is string path
            ? $"path:{path}"
            : $"name:{options.SocketName ?? "default"}",
        "unknown",
        "unknown",
        ServerState: "unknown",
        TeardownExplicitlySelected: false);

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
