using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Server
{
    /// <summary>Binds a key to a tmux command.</summary>
    /// <param name="request">Which key, to what, and in which table.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task BindKeyAsync(BindKeyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunUtilityAsync(BuildBindKeyArguments(request), cancellationToken);
    }

    internal static List<string> BuildBindKeyArguments(BindKeyRequest request)
    {
        List<string> arguments = ["bind-key"];
        ServerUtilities.AddFlag(arguments, request.Repeat, "-r");
        ServerUtilities.AddValue(arguments, "-T", request.KeyTable);
        ServerUtilities.AddValue(arguments, "-N", request.Note);
        arguments.Add(request.Key);
        arguments.AddRange(request.Command);
        return arguments;
    }

    /// <summary>Removes a key binding.</summary>
    /// <param name="request">Which key, or every key in a table.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task UnbindKeyAsync(
        UnbindKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunUtilityAsync(BuildUnbindKeyArguments(request), cancellationToken);
    }

    internal static List<string> BuildUnbindKeyArguments(UnbindKeyRequest request)
    {
        List<string> arguments = ["unbind-key"];
        ServerUtilities.AddFlag(arguments, request.All, "-a");
        ServerUtilities.AddFlag(arguments, request.Quiet, "-q");
        ServerUtilities.AddValue(arguments, "-T", request.KeyTable);

        // tmux still wants a key after the all flag, and takes any one.
        arguments.Add(request.Key ?? "-a");
        return arguments;
    }

    /// <summary>Reads the key bindings.</summary>
    /// <param name="keyTable">The table to read, or null for every table.</param>
    /// <param name="format">The tmux format each binding is rendered with.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>One line per binding, as tmux rendered it.</returns>
    /// <remarks>Rendering with a format arrived in tmux 3.7.</remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>> GetKeysAsync(
        string? keyTable = null,
        string? format = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["list-keys"];
        ServerUtilities.AddValue(arguments, "-T", keyTable);
        if (format is not null
            && RequiresCapability(ServerUtilities.ListKeysFormatCapability, LogListKeysFormat))
        {
            ServerUtilities.AddValue(arguments, "-F", format);
        }

        return await ReadUtilityAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
