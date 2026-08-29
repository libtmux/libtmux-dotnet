using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace LibTmux.Mcp;

/// <summary>Serves tmux over the Model Context Protocol.</summary>
/// <remarks>
/// <para>
/// The protocol speaks over standard output, so every log line has to go to
/// standard error. A message written to the wrong stream is not a stray line
/// in a log: it corrupts the protocol and the client disconnects.
/// </para>
/// <para>
/// The session is run directly rather than as a hosted service. A generic host
/// stops the application as soon as standard input reaches end of file, which
/// races the reply still being written — measured: a client that wrote one
/// <c>initialize</c> frame and closed stdin got no answer at all, while the
/// same frame followed by a one second pause was answered. Owning the lifetime
/// here means the process ends when the session does, not before.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync("tmux does not run on Windows.").ConfigureAwait(false);
            return 1;
        }

        ServiceCollection services = new();
        services.AddLogging(logging =>
        {
            logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

            // Quiet by default. A client that surfaces standard error shows the
            // user whatever is written there, and per-request chatter turns a
            // working server into something that looks broken.
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        // The library configures every await away from a caller's context. This
        // is the entry point rather than the library: there is no context here
        // to return to, so these say nothing about it.
        await using ServiceProvider provider = BuildProvider(services, args);
        ILoggerFactory logging = provider.GetRequiredService<ILoggerFactory>();

        // The transport buffers standard output, so it is held and disposed
        // here rather than left to the collector: an undisposed buffer means a
        // reply that was written and never flushed, which a client sees as no
        // reply at all.
        await using StdioServerTransport transport = new("tmux", logging);
        await using McpServer server = McpServer.Create(
            transport,
            provider.GetRequiredService<IOptions<McpServerOptions>>().Value,
            logging,
            provider);

        await server.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static ServiceProvider BuildProvider(ServiceCollection services, string[] args)
    {
        using ILoggerFactory startup = LoggerFactory.Create(logging =>
            logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));

        ServerPolicy policy = ServerPolicy.FromEnvironment(
            System.Environment.GetEnvironmentVariable,
            startup.CreateLogger(nameof(ServerPolicy)));

        // A socket named on the command line lets one assistant drive a server
        // that is not the ambient one, which is what a test or a sandbox wants.
        string? socket = args.Length > 0 ? args[0] : policy.DefaultSocketName;

        McpServerComposition.Add(
            services,
            policy,
            new ServerConnectionOptions(socketName: socket),
            TmuxTargets.CallerPaneId());

        return services.BuildServiceProvider();
    }
}
