using System.Runtime.Versioning;

namespace LibTmux;

/// <summary>The key bindings of one server.</summary>
/// <remarks>
/// Bindings live in named tables. The prefix table is what a key reaches after
/// the prefix key, and <c>root</c> is what it reaches without one.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class TmuxKeys
{
    private readonly Server _server;

    internal TmuxKeys(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
    }

    /// <summary>Binds a key to a tmux command.</summary>
    /// <param name="request">Which key, to what, and in which table.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A task that completes once the binding is in place.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    public Task BindAsync(
        BindKeyRequest request,
        CancellationToken cancellationToken = default) =>
        _server.BindKeyAsync(request, cancellationToken);

    /// <summary>Removes a binding, or every binding in a table.</summary>
    /// <param name="request">Which key, or every key in a table.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A task that completes once the binding is gone.</returns>
    /// <remarks>
    /// Removing a binding nobody made is not an error: tmux treats the
    /// absence as the state that was asked for.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    /// <exception cref="ArgumentException">The request names no key and does not ask for all.</exception>
    public Task UnbindAsync(
        UnbindKeyRequest request,
        CancellationToken cancellationToken = default) =>
        _server.UnbindKeyAsync(request, cancellationToken);

    /// <summary>Reads the bindings as tmux rendered them.</summary>
    /// <param name="keyTable">The table to read, or null for every table.</param>
    /// <param name="format">The tmux format each binding is rendered with.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>One line per binding.</returns>
    /// <remarks>Rendering with a format arrived in tmux 3.7.</remarks>
    public Task<IReadOnlyList<string>> GetAllAsync(
        string? keyTable = null,
        string? format = null,
        CancellationToken cancellationToken = default) =>
        _server.GetKeysAsync(keyTable, format, cancellationToken);
}
