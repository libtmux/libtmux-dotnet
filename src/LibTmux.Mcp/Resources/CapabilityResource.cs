using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibTmux.Internal;
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
    /// <summary>The selector reported when no socket was chosen and tmux picks the server.</summary>
    internal const string DefaultSelector = "default";

    internal static McpRuntimeDisclosure Unknown(ServerConnectionOptions options)
    {
        string resolvedPath = options.SocketPath is string path ? Path.GetFullPath(path) : "";
        return new(
            resolvedPath.Length > 0
                ? $"path:{resolvedPath}"
                : options.SocketName is string name ? $"name:{name}" : DefaultSelector,
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
        string binary = ShellQuote(options.TmuxBinaryPath);
        if (resolvedSocketPath.Length > 0)
        {
            return $"{binary} -N -S {ShellQuote(resolvedSocketPath)} attach";
        }

        // With no socket chosen the command carries none, so tmux picks the
        // same server for the operator that it picked for this one.
        return options.SocketName is string name
            ? $"{binary} -N -L {ShellQuote(name)} attach"
            : $"{binary} -N attach";
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
    private readonly ServerPolicy _policy;

    public CapabilityResource(
        CapabilityRegistry registry, McpRuntimeDisclosure runtime, ServerPolicy? policy = null)
    {
        _registry = registry;
        _runtime = runtime;
        _policy = policy ?? new ServerPolicy();
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
            ["paneObservation"] = new JsonObject
            {
                ["controlRequired"] = !_policy.AllowPollingFallback,
                ["pollingFallbackAllowed"] = _policy.AllowPollingFallback,
                ["pollsWithoutOptInBelow"] = "3.7",
                ["pollIntervalMilliseconds"] = _policy.AllowPollingFallback
                    ? PaneActivityHub.PollInterval.TotalMilliseconds : null,
                ["activationResultField"] = "pollingFallback",
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
                ["minimumTmuxVersion"] = LibTmuxInfo.MinimumTmuxVersion.Raw,
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
