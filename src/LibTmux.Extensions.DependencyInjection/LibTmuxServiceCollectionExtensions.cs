using LibTmux;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers LibTmux with a service collection.</summary>
public static class LibTmuxServiceCollectionExtensions
{
    /// <summary>Registers a tmux server handle and the options it reads.</summary>
    /// <param name="services">The collection to register in.</param>
    /// <param name="configure">
    /// Returns the options to connect with, given the ones bound so far. The
    /// options are immutable, so this answers a copy:
    /// <c>options => options with { SocketName = "build" }</c>.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The handle is a singleton. It is immutable and safe to share, and it is
    /// opened rather than connected, so nothing runs tmux until something asks
    /// it to and a server that restarts is picked up by the next call.
    /// </para>
    /// <para>
    /// Options bind from configuration as
    /// <c>services.Configure&lt;ServerConnectionOptions&gt;(section)</c>
    /// before <paramref name="configure" /> runs. When they name no logger and
    /// the provider has an <see cref="ILoggerFactory" />, one is taken from it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    public static IServiceCollection AddLibTmux(
        this IServiceCollection services,
        Func<ServerConnectionOptions, ServerConnectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<ServerConnectionOptions>();
        services.TryAddSingleton(provider => Open(provider, configure));
        return services;
    }

    private static Server Open(
        IServiceProvider provider,
        Func<ServerConnectionOptions, ServerConnectionOptions>? configure)
    {
        ServerConnectionOptions options = provider
            .GetRequiredService<IOptions<ServerConnectionOptions>>()
            .Value;
        if (configure is not null)
        {
            options = configure(options);
        }

        if (options.Logger is null
            && provider.GetService<ILoggerFactory>() is ILoggerFactory factory)
        {
            options = options with { Logger = factory.CreateLogger("LibTmux") };
        }

        return Server.Open(options);
    }
}
