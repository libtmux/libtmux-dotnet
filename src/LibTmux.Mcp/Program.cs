using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
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
/// The SDK owns protocol dispatch. Input EOF cancels unfinished requests, and
/// the session finishes before its services are disposed. A disconnected
/// client is not guaranteed replies to requests that were still pending.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class Program
{
    private static async Task<int> Main()
    {
        if (OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync("tmux does not run on Windows.").ConfigureAwait(false);
            return 1;
        }

        try
        {
            return await ServeAsync().ConfigureAwait(false);
        }
        catch (McpException error)
        {
            // A misconfigured environment is a message, not a crash. Letting it
            // escape ended the process with SIGABRT and a .NET stack trace
            // carrying absolute source paths, which reads as a broken server
            // rather than as a variable to correct.
            await Console.Error.WriteLineAsync(error.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> ServeAsync()
    {
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
        await using ServiceProvider provider = await BuildProviderAsync(services)
            .ConfigureAwait(false);
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

        using CancellationTokenSource shutdown = new();
        Task running = server.RunAsync(shutdown.Token);
        await Task.WhenAny(running, transport.MessageReader.Completion).ConfigureAwait(false);
        if (!running.IsCompleted)
        {
            // The SDK drains handlers after EOF without cancelling them. End
            // their waits before disposing the observer and owned daemon.
            await shutdown.CancelAsync().ConfigureAwait(false);
        }
        await running.ConfigureAwait(false);
        return 0;
    }

    private static async Task<ServiceProvider> BuildProviderAsync(ServiceCollection services)
    {
        using ILoggerFactory startup = LoggerFactory.Create(logging =>
            logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));

        ServerPolicy policy = ServerPolicy.FromEnvironment(
            System.Environment.GetEnvironmentVariable,
            startup.CreateLogger(nameof(ServerPolicy)));
        McpStartup resolved = await McpStartup.ResolveAsync(
                System.Environment.GetEnvironmentVariable)
            .ConfigureAwait(false);

        McpServerComposition.Add(
            services,
            policy,
            resolved.ConnectionOptions,
            TmuxTargets.CallerPaneIdOn(resolved.Disclosure.ResolvedSocketPath),
            resolved.Selection,
            resolved.Disclosure);

        services.AddSingleton<McpStartup>(_ => resolved);
        ServiceProvider provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<McpStartup>();
        return provider;
    }
}
