using System.Runtime.Versioning;

namespace LibTmux.Testing;

/// <summary>Owns a throwaway window and any private session and server created with it.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class TemporaryWindowScope : IAsyncDisposable
{
    private readonly OwnedWindowScope _owned;
    private readonly IAsyncDisposable? _parent;
    private int _disposed;

    private TemporaryWindowScope(OwnedWindowScope owned, IAsyncDisposable? parent)
    {
        _owned = owned;
        _parent = parent;
        Window = owned.Value;
    }

    /// <summary>Gets the temporary window.</summary>
    public Window Window { get; }

    /// <summary>Creates a temporary window in a session.</summary>
    /// <param name="session">The session to create the window in.</param>
    /// <param name="request">The window to create.</param>
    /// <param name="parent">The parent scope transferred into this scope.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The scope owning the window.</returns>
    [UnsupportedOSPlatform("windows")]
    internal static async Task<TemporaryWindowScope> StartAsync(
        Session session,
        NewWindowRequest? request = null,
        IAsyncDisposable? parent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        OwnedWindowScope owned = await session
            .CreateOwnedWindowAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return new TemporaryWindowScope(owned, parent);
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
