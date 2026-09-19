using System.Runtime.Versioning;

namespace LibTmux;

/// <summary>The paste buffers of one server.</summary>
/// <remarks>
/// tmux keeps buffers on the server rather than in a session, so a buffer set
/// from one session is readable from every other. A buffer named null is the
/// most recent one, which is what tmux pastes when nothing names a buffer.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class TmuxBuffers
{
    private readonly Server _server;

    internal TmuxBuffers(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
    }

    /// <summary>Puts text into a buffer.</summary>
    /// <param name="data">The text to store.</param>
    /// <param name="name">The buffer name, or null for a new one.</param>
    /// <param name="append">Whether the text joins what is already there.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A task that completes once tmux has stored it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data" /> is null.</exception>
    public Task SetAsync(
        string data,
        string? name = null,
        bool append = false,
        CancellationToken cancellationToken = default) =>
        _server.SetBufferAsync(data, name, append, cancellationToken);

    /// <summary>Puts a file's contents into a buffer.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="name">The buffer name, or null for a new one.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A task that completes once tmux has read it.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is blank.</exception>
    public Task LoadAsync(
        string path,
        string? name = null,
        CancellationToken cancellationToken = default) =>
        _server.LoadBufferAsync(path, name, cancellationToken);

    /// <summary>Writes a buffer to a file.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="name">The buffer to write, or null for the most recent.</param>
    /// <param name="append">Whether the buffer joins what the file already holds.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A task that completes once tmux has written it.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is blank.</exception>
    public Task SaveAsync(
        string path,
        string? name = null,
        bool append = false,
        CancellationToken cancellationToken = default) =>
        _server.SaveBufferAsync(path, name, append, cancellationToken);

    /// <summary>Reads a buffer in full.</summary>
    /// <param name="name">The buffer to read, or null for the most recent.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>Everything the buffer holds.</returns>
    public Task<string> GetAsync(
        string? name = null,
        CancellationToken cancellationToken = default) =>
        _server.GetBufferAsync(name, cancellationToken);

    /// <summary>Forgets a buffer.</summary>
    /// <param name="name">The buffer to forget, or null for the most recent.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A task that completes once tmux has forgotten it.</returns>
    public Task DeleteAsync(
        string? name = null,
        CancellationToken cancellationToken = default) =>
        _server.DeleteBufferAsync(name, cancellationToken);

    /// <summary>Reads every buffer.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>Each buffer, with its size and a sample of its contents.</returns>
    public Task<IReadOnlyList<TmuxBuffer>> GetAllAsync(
        CancellationToken cancellationToken = default) =>
        _server.GetBuffersAsync(cancellationToken);

    /// <summary>Reads the buffers as tmux rendered them.</summary>
    /// <param name="request">The format and filter, or null for tmux's own.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>One line per buffer.</returns>
    public Task<IReadOnlyList<string>> GetLinesAsync(
        ListBuffersRequest? request = null,
        CancellationToken cancellationToken = default) =>
        _server.GetBufferLinesAsync(request, cancellationToken);
}
