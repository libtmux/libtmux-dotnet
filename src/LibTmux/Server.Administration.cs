using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Server
{
    /// <summary>Builds the arguments an access request sends.</summary>
    /// <remarks>
    /// The command itself arrived in tmux 3.3, so the refusal belongs here
    /// rather than beside the dispatch: a chained request that skipped it
    /// would send a command older servers do not have.
    /// </remarks>
    /// <exception cref="TmuxVersionTooLowException">tmux is older than 3.3.</exception>
    internal List<string> BuildServerAccessArguments(ServerAccessRequest request)
    {
        RequireCommand(ServerUtilities.ServerAccessCapability, "server-access");
        List<string> arguments = ["server-access"];
        ServerUtilities.AddFlag(arguments, request.AllowUser is not null, "-a");
        ServerUtilities.AddFlag(arguments, request.DenyUser is not null, "-d");
        ServerUtilities.AddFlag(arguments, request.List, "-l");
        ServerUtilities.AddFlag(arguments, request.ReadOnly, "-r");
        ServerUtilities.AddFlag(arguments, request.ReadWrite, "-w");
        if ((request.AllowUser ?? request.DenyUser) is string user)
        {
            arguments.Add(user);
        }

        return arguments;
    }

    /// <summary>Grants or withdraws another user's access to this server.</summary>
    /// <param name="request">Who, and what they may do.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The current list when it was asked for, and null otherwise.</returns>
    /// <exception cref="TmuxVersionTooLowException">tmux is older than 3.3.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>?> ConfigureAccessAsync(
        ServerAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildServerAccessArguments(request);

        IReadOnlyList<string> lines = await ReadUtilityAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        return request.List ? lines : null;
    }

    /// <summary>Reads a tmux configuration file.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="quiet">Whether a missing file is passed over in silence.</param>
    /// <param name="parseOnly">Whether the file is checked rather than run.</param>
    /// <param name="verbose">Whether each command read is reported.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task SourceFileAsync(
        string path,
        bool quiet = false,
        bool parseOnly = false,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        List<string> arguments = ["source-file"];
        ServerUtilities.AddFlag(arguments, quiet, "-q");
        ServerUtilities.AddFlag(arguments, parseOnly, "-n");
        ServerUtilities.AddFlag(arguments, verbose, "-v");
        arguments.Add(path);
        return RunUtilityAsync(arguments, cancellationToken);
    }

    /// <summary>Locks every client attached to this server.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task LockAsync(CancellationToken cancellationToken = default) =>
        RunUtilityAsync(["lock-server"], cancellationToken);

    /// <summary>Reads what the server has been logging.</summary>
    /// <param name="targetClient">The client to read for, or null for the server.</param>
    /// <param name="mode">Which log to read.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>One line per entry.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>> GetMessagesAsync(
        string? targetClient = null,
        ShowMessagesMode mode = ShowMessagesMode.Messages,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["show-messages"];
        if (ServerUtilities.GetShowMessagesFlag(mode) is string flag)
        {
            arguments.Add(flag);
        }

        ServerUtilities.AddValue(arguments, "-t", targetClient);
        return await ReadUtilityAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the commands this tmux knows.</summary>
    /// <param name="name">One command to describe, or null for all of them.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>One line per command, giving its syntax.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>> GetCommandsAsync(
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["list-commands"];
        if (name is not null)
        {
            arguments.Add(name);
        }

        return await ReadUtilityAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
