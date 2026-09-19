using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Server
{
    [UnsupportedOSPlatform("windows")]
    internal Task SetBufferAsync(
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

    [UnsupportedOSPlatform("windows")]
    internal Task LoadBufferAsync(
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

    [UnsupportedOSPlatform("windows")]
    internal Task SaveBufferAsync(
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

    [UnsupportedOSPlatform("windows")]
    internal async Task<string> GetBufferAsync(
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["show-buffer"];
        ServerUtilities.AddValue(arguments, "-b", name);
        IReadOnlyList<string> lines = await ReadUtilityAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        return string.Join('\n', lines);
    }

    [UnsupportedOSPlatform("windows")]
    internal Task DeleteBufferAsync(
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

    [UnsupportedOSPlatform("windows")]
    internal async Task<IReadOnlyList<TmuxBuffer>> GetBuffersAsync(
        CancellationToken cancellationToken = default) =>
        ServerUtilities.ReadBuffers(
            await ReadUtilityAsync(["list-buffers"], cancellationToken).ConfigureAwait(false));

    [UnsupportedOSPlatform("windows")]
    internal async Task<IReadOnlyList<string>> GetBufferLinesAsync(
        ListBuffersRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = BuildListBuffersArguments(request);
        return await ReadUtilityAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
