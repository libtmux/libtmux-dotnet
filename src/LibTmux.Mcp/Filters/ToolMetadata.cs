using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace LibTmux.Mcp;

[UnsupportedOSPlatform("windows")]
internal static class ToolMetadata
{
    /// <summary>Finds what this server declared about one tool.</summary>
    /// <param name="services">The services the call was answered with.</param>
    /// <param name="tool">The tool named by the call.</param>
    /// <returns>The declaration, or null for a tool this server did not declare.</returns>
    internal static ToolDefinition? Declaration(IServiceProvider? services, string tool) =>
        services?.GetService<CapabilityRegistry>() is CapabilityRegistry registry
            && registry.DispatchByName.TryGetValue(tool, out ToolDefinition? definition)
                ? definition
                : null;

    /// <summary>Answers whether a tool could have changed tmux before it failed.</summary>
    /// <param name="services">The services the call was answered with.</param>
    /// <param name="tool">The tool named by the call.</param>
    /// <returns><see langword="true" /> unless the tool only observes.</returns>
    /// <remarks>
    /// This server's own tools are read from their declared effects, not from
    /// the protocol's readOnlyHint. Every one of them advertises readOnlyHint
    /// false deliberately — the capability model refuses an optimistic
    /// annotation — so that hint says nothing about what they do, and reading
    /// it told a caller that a failed list_sessions might have acted. A tool
    /// whose only effect is Observe cannot have changed anything. A tool this
    /// server did not declare is judged by the annotation it did declare, and
    /// anything still unknown is treated as a mutation, because the advice it
    /// earns is the cautious one.
    /// </remarks>
    internal static bool MayModify(IServiceProvider? services, string tool)
    {
        if (Declaration(services, tool) is ToolDefinition definition)
        {
            return !definition.Effects.SetEquals([Effect.Observe]);
        }

        McpServerOptions? options = services
            ?.GetService<IOptions<McpServerOptions>>()?.Value;
        McpServerTool? registered = options?.ToolCollection?
            .FirstOrDefault(candidate => string.Equals(
                candidate.ProtocolTool.Name,
                tool,
                StringComparison.Ordinal));
        return registered?.ProtocolTool.Annotations?.ReadOnlyHint != true;
    }
}
