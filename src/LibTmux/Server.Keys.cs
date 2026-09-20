using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Server
{
    [UnsupportedOSPlatform("windows")]
    internal Task BindKeyAsync(BindKeyRequest request, CancellationToken cancellationToken = default)
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
        ServerUtilities.EndOptions(arguments);
        arguments.Add(request.Key);
        arguments.AddRange(request.Command);
        return arguments;
    }

    [UnsupportedOSPlatform("windows")]
    internal Task UnbindKeyAsync(
        UnbindKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunUtilityAsync(BuildUnbindKeyArguments(request), cancellationToken);
    }

    internal static List<string> BuildUnbindKeyArguments(UnbindKeyRequest request)
    {
        request.Validate();
        List<string> arguments = ["unbind-key"];
        ServerUtilities.AddFlag(arguments, request.All, "-a");
        ServerUtilities.AddFlag(arguments, request.Quiet, "-q");
        ServerUtilities.AddValue(arguments, "-T", request.KeyTable);

        // tmux takes no key argument at all alongside -a; naming one anyway
        // is redundant and tmux refuses it as its own error.
        if (request.Key is not null)
        {
            ServerUtilities.EndOptions(arguments);
            arguments.Add(request.Key);
        }

        return arguments;
    }

    [UnsupportedOSPlatform("windows")]
    internal async Task<IReadOnlyList<string>> GetKeysAsync(
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
