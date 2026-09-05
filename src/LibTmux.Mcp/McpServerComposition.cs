using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LibTmux.Mcp;

/// <summary>Assembles the server: what it offers, and what it refuses to.</summary>
/// <remarks>
/// One description of the wiring, used by the executable and by the tests that
/// check what a client receives. Two descriptions would drift, and the one
/// that drifted would be the tested one.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public static class McpServerComposition
{
    /// <summary>Registers everything the server needs and everything it offers.</summary>
    /// <param name="services">The container to register into.</param>
    /// <param name="policy">What the server will do.</param>
    /// <param name="connectionOptions">How to reach tmux.</param>
    /// <param name="callerPaneId">The pane the server runs in, when it runs in one.</param>
    /// <returns>The builder, for a transport to be chosen on.</returns>
    public static IMcpServerBuilder Add(
        IServiceCollection services,
        ServerPolicy policy,
        ServerConnectionOptions connectionOptions,
        string? callerPaneId) => Add(
            services,
            policy,
            connectionOptions,
            callerPaneId,
            CapabilitySelection.WithoutTeardown,
            McpRuntimeDisclosure.Unknown(connectionOptions));

    internal static IMcpServerBuilder Add(
        IServiceCollection services,
        ServerPolicy policy,
        ServerConnectionOptions connectionOptions,
        string? callerPaneId,
        CapabilitySelection selection,
        McpRuntimeDisclosure runtime)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(connectionOptions);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(runtime);

        CapabilityRegistry registry = CapabilityRegistry.Select(selection);

        services.AddSingleton(policy);
        services.AddSingleton(registry);
        services.AddSingleton(runtime);
        services.AddSingleton(provider => new TmuxConnectionAccessor(
            connectionOptions,
            connectionOptions.SocketName,
            provider.GetService<ILoggerFactory>()?.CreateLogger<TmuxConnectionAccessor>()));
        services.AddSingleton(provider => new PaneActivityHub(
            provider.GetService<ILoggerFactory>()?.CreateLogger<PaneActivityHub>()));
        services.AddSingleton<ReadTools>();
        services.AddSingleton<WriteTools>();
        services.AddSingleton<CapabilityTools>();
        services.AddSingleton<CapabilityResource>();
        var taskStore = new BoundedMcpTaskStore();

        IMcpServerBuilder builder = services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "tmux",
                    Version = LibTmuxMcp.Version,
                };
                options.ServerInstructions = ServerInstructions.Compose(policy, callerPaneId);
            })
            .WithTools(registry.Tools)
            .WithResources<CapabilityResource>()
            .WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(next =>
                    ToolResponseBudgetFilter.Create(policy)(ToolFailureFilter.Create()(next)));
                filters.AddReadResourceFilter(ResourceResponseBudgetFilter.Create(policy));
            })

            // Task-capable clients may collect a wait later; other clients block.
            .WithTasks(
                taskStore,
                tasks => tasks.ExecutionModeSelector = TaskCapableTools.Select);

        services.AddSingleton<IConfigureOptions<McpServerOptions>>(
            new BoundedMcpTaskCancellationOptions(taskStore));

        return builder;
    }
}
