using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Server
{
    /// <summary>Puts text into a paste buffer.</summary>
    /// <param name="data">The text to store.</param>
    /// <param name="name">The buffer name, or null for a new one.</param>
    /// <param name="append">Whether the text joins what is already there.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task SetBufferAsync(
        string data,
        string? name = null,
        bool append = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        List<string> arguments = ["set-buffer"];
        ServerUtilities.AddFlag(arguments, append, "-a");
        ServerUtilities.AddValue(arguments, "-b", name);
        arguments.Add(data);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    /// <summary>Puts a file's contents into a paste buffer.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="name">The buffer name, or null for a new one.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task LoadBufferAsync(
        string path,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        List<string> arguments = ["load-buffer"];
        ServerUtilities.AddValue(arguments, "-b", name);
        arguments.Add(path);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    /// <summary>Writes a paste buffer to a file.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="name">The buffer to write, or null for the most recent.</param>
    /// <param name="append">Whether the buffer joins what the file already holds.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task SaveBufferAsync(
        string path,
        string? name = null,
        bool append = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        List<string> arguments = ["save-buffer"];
        ServerUtilities.AddFlag(arguments, append, "-a");
        ServerUtilities.AddValue(arguments, "-b", name);
        arguments.Add(path);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    /// <summary>Reads a paste buffer in full.</summary>
    /// <param name="name">The buffer to read, or null for the most recent.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>Everything the buffer holds.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<string> GetBufferAsync(
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["show-buffer"];
        ServerUtilities.AddValue(arguments, "-b", name);
        IReadOnlyList<string> lines = await ReadUtilityAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        return string.Join('\n', lines);
    }

    /// <summary>Forgets a paste buffer.</summary>
    /// <param name="name">The buffer to forget, or null for the most recent.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task DeleteBufferAsync(
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["delete-buffer"];
        ServerUtilities.AddValue(arguments, "-b", name);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    internal static List<string> BuildListBuffersArguments(ListBuffersRequest? request)
    {
        List<string> arguments = ["list-buffers"];
        ServerUtilities.AddValue(arguments, "-F", request?.Format);
        ServerUtilities.AddValue(arguments, "-f", request?.Filter?.Value);

        return arguments;
    }

    /// <summary>Reads the paste buffers.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>Every buffer, with its size and a sample of its contents.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<TmuxBuffer>> GetBuffersAsync(
        CancellationToken cancellationToken = default) =>
        ServerUtilities.ReadBuffers(
            await ReadUtilityAsync(["list-buffers"], cancellationToken).ConfigureAwait(false));

    /// <summary>Reads the paste buffers as tmux rendered them.</summary>
    /// <param name="request">The format and filter, or null for tmux's own.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>One line per buffer.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>> GetBufferLinesAsync(
        ListBuffersRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = BuildListBuffersArguments(request);
        return await ReadUtilityAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
