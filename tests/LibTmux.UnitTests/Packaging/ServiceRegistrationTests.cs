using System.Runtime.Versioning;
using LibTmux.UnitTests.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibTmux.UnitTests.Packaging;

/// <summary>What AddLibTmux puts in a container, and what it reads to build it.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class ServiceRegistrationTests
{
    [UnixFact]
    public void A_registered_server_is_one_shared_unmaterialized_handle()
    {
        ServiceProvider provider = new ServiceCollection().AddLibTmux().BuildServiceProvider();

        var server = provider.GetRequiredService<Server>();

        // Opened, not connected: nothing has run tmux yet.
        Assert.False(server.IsMaterialized);
        Assert.Same(server, provider.GetRequiredService<Server>());
    }

    [UnixFact]
    public void Configuration_binds_first_and_the_callback_answers_last()
    {
        ServerConnectionOptions? given = null;
        ServiceProvider provider = new ServiceCollection()
            .Configure<ServerConnectionOptions>(options => BindSocketName(options, "bound"))
            .AddLibTmux(options =>
            {
                given = options;
                return options with { SocketName = options.SocketName + "-then-mine" };
            })
            .BuildServiceProvider();

        provider.GetRequiredService<Server>();

        Assert.Equal("bound", given?.SocketName);
    }

    [UnixFact]
    public void A_logger_factory_answers_when_the_options_name_no_logger()
    {
        var factory = new RecordingLoggerFactory();
        ServiceProvider provider = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(factory)
            .AddLibTmux()
            .BuildServiceProvider();

        provider.GetRequiredService<Server>();

        Assert.Equal("LibTmux", factory.Category);
    }

    /// <summary>Sets a socket name the way the configuration binder does.</summary>
    /// <remarks>
    /// The options are immutable, so a configuration section reaches them
    /// through reflection rather than through an init setter a caller could
    /// write.
    /// </remarks>
    private static void BindSocketName(ServerConnectionOptions options, string name) =>
        typeof(ServerConnectionOptions)
            .GetProperty(nameof(ServerConnectionOptions.SocketName))!
            .SetValue(options, name);

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        internal string? Category { get; private set; }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            Category = categoryName;
            return NullLogger.Instance;
        }

        public void Dispose()
        {
        }
    }
}
