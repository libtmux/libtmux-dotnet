using System.Runtime.Versioning;

namespace LibTmux.Testing;

/// <summary>Owns a throwaway session and any private server created with it.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class TemporarySessionScope : IAsyncDisposable
{
    private readonly OwnedSessionScope _owned;
    private readonly IAsyncDisposable? _parent;
    private int _disposed;

    private TemporarySessionScope(OwnedSessionScope owned, IAsyncDisposable? parent)
    {
        _owned = owned;
        _parent = parent;
        Session = owned.Value;
    }

    /// <summary>Gets the temporary session.</summary>
    public Session Session { get; }

    /// <summary>Creates a temporary session on a running server.</summary>
    /// <param name="server">The server to create the session on.</param>
    /// <param name="request">The session to create.</param>
    /// <param name="parent">The parent scope transferred into this scope.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The scope owning the session.</returns>
    [UnsupportedOSPlatform("windows")]
    internal static async Task<TemporarySessionScope> StartAsync(
        Server server,
        NewSessionRequest? request = null,
        IAsyncDisposable? parent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        OwnedSessionScope owned = await server
            .CreateOwnedSessionAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return new TemporarySessionScope(owned, parent);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await TemporaryScopeCleanup.DisposeAsync(_owned, _parent).ConfigureAwait(false);
    }
}
