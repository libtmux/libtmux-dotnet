using System.Runtime.Versioning;

namespace LibTmux.Testing;

/// <summary>A server, session, window, and pane a test owns together.</summary>
/// <remarks>
/// Most tests want somewhere to type, which is four objects deep. Making them
/// one at a time leaves a test holding four scopes and disposing them in the
/// right order; one scope owns the lot and unwinds from the server, which takes
/// everything inside it with it.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class TemporaryHierarchyScope : IAsyncDisposable
{
    private readonly TemporaryServerScope _scope;

    internal TemporaryHierarchyScope(
        TemporaryServerScope scope,
        Session session,
        Window window,
        Pane pane)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(pane);
        _scope = scope;
        Session = session;
        Window = window;
        Pane = pane;
    }

    /// <summary>Gets the server the rest live in.</summary>
    /// <remarks>
    /// Creating the session materializes its server. The returned handle carries
    /// that generation and version, so it is ready for listings and lookups.
    /// </remarks>
    public Server Server => Session.Server;

    /// <summary>Gets the session.</summary>
    public Session Session { get; }

    /// <summary>Gets the window.</summary>
    public Window Window { get; }

    /// <summary>Gets the pane.</summary>
    public Pane Pane { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Only the server is unwound: killing it takes the session, window, and
    /// pane with it, and asking tmux to remove each first would race that.
    /// </remarks>
    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
