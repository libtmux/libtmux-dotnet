using System.IO.Pipelines;
using System.Runtime.Versioning;
using LibTmux.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LibTmux.Examples;

/// <summary>A real MCP client and server connected to an example's tmux server.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class ExampleMcpConnection : IAsyncDisposable
{
    private readonly McpServer _server;
    private readonly ServiceProvider _services;

    private ExampleMcpConnection(
        McpServer server,
        McpClient client,
        ServiceProvider services)
    {
        _server = server;
        Client = client;
        _services = services;
    }

    internal McpClient Client { get; }

    internal static async Task<ExampleMcpConnection> OpenAsync(
        Server tmux,
        CancellationToken cancellationToken)
    {
        ServiceCollection services = new();
        services.AddLogging();
        McpServerComposition.Add(
            services,
            new ServerPolicy(),
            tmux.ConnectionOptions,
            callerPaneId: null);
        ServiceProvider provider = services.BuildServiceProvider();

        Pipe clientToServer = new();
        Pipe serverToClient = new();
        McpServer server = McpServer.Create(
            new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream()),
            provider.GetRequiredService<IOptions<McpServerOptions>>().Value,
            provider.GetRequiredService<ILoggerFactory>(),
            provider);
        _ = server.RunAsync(CancellationToken.None);

        try
        {
            McpClient client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    clientToServer.Writer.AsStream(),
                    serverToClient.Reader.AsStream()),
                cancellationToken: cancellationToken);
            return new ExampleMcpConnection(server, client, provider);
        }
        catch
        {
            await server.DisposeAsync();
            await provider.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _server.DisposeAsync();
        await _services.DisposeAsync();
    }
}
