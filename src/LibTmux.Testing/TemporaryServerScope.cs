using System.Runtime.Versioning;

namespace LibTmux.Testing;

/// <summary>Creates a throwaway server for a test and stops it afterwards.</summary>
/// <remarks>
/// Each scope gets its own socket, so tests never contend for one server and a
/// crashed test cannot leak state into the next.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class TemporaryServerScope : IAsyncDisposable
{
    private readonly OwnedServerScope _owned;

    private TemporaryServerScope(OwnedServerScope owned)
    {
        _owned = owned;
        Server = owned.Value;
    }

    /// <summary>Gets the temporary server.</summary>
    public Server Server { get; }

    /// <summary>Starts a temporary server on its own socket.</summary>
    /// <param name="options">Connection options, or null for a private socket.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The scope owning the server.</returns>
    [UnsupportedOSPlatform("windows")]
    internal static async Task<TemporaryServerScope> StartAsync(
        ServerConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        OwnedServerScope owned = await LibTmux.Server
            .CreateOwnedAsync(options ?? Isolated(), cancellationToken)
            .ConfigureAwait(false);
        return new TemporaryServerScope(owned);
    }

    // An unconfigured scope must not fall back to the ambient connection: it
    // would point at the developer's own server and kill it on disposal.
    private static ServerConnectionOptions Isolated() =>
        new(socketName: $"libtmux-{Guid.NewGuid():N}");

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _owned.DisposeAsync();
}
