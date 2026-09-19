using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Provides raw command execution for a tmux server endpoint.
public sealed partial class Server
{
    private readonly TmuxCommandDispatcher _commandDispatcher;

    internal Server(TmuxCommandDispatcher commandDispatcher)
    {
        _commandDispatcher = commandDispatcher
            ?? throw new ArgumentNullException(nameof(commandDispatcher));
    }

    /// <summary>Executes one raw tmux command.</summary>
    [UnsupportedOSPlatform("windows")]
    public Task<TmuxCommandResult> ExecuteCommandAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        _commandDispatcher.ExecuteAsync(arguments, cancellationToken);

    /// <summary>
    /// Gets the endpoint selection arguments -- <c>-L</c>, <c>-S</c>, or a
    /// configuration file flag -- this handle's connection resolved.
    /// </summary>
    /// <remarks>
    /// Prepend these to <see cref="BuildGuardedCommandLine" />'s result, and
    /// to <see cref="ServerConnectionOptions.TmuxBinaryPath" />, to invoke tmux
    /// as this process's own child rather than through this library's
    /// dispatch -- the shape an interactive attach needs, since attaching must
    /// inherit the caller's own terminal.
    /// </remarks>
    public IReadOnlyList<string> EndpointArguments => _connection?.PrefixArguments ?? [];

    /// <summary>
    /// Builds the argument vector for a raw tmux invocation that only takes
    /// effect while the named server generation is still current.
    /// </summary>
    /// <param name="expected">The generation the command requires.</param>
    /// <param name="mismatchCommand">
    /// A tmux command run instead when the generation has changed. The caller
    /// already committed to spawning a process expecting
    /// <paramref name="expected" />, so this is typically one that fails
    /// loudly rather than one that repairs anything.
    /// </param>
    /// <param name="commands">
    /// One or more tmux commands to run when the generation still matches,
    /// each its own argument vector.
    /// </param>
    /// <returns>
    /// The encoded arguments. Prepend <see cref="EndpointArguments" /> before
    /// passing this to a process started at
    /// <see cref="ServerConnectionOptions.TmuxBinaryPath" />.
    /// </returns>
    /// <remarks>
    /// A command this library dispatches itself is already guarded by
    /// <see cref="TmuxCommand.RequiredGeneration" /> through
    /// <see cref="Server.Chain" />. This is for the one case that cannot go
    /// through that dispatch -- an interactive attach, which must run as its
    /// own foreground process to inherit the caller's terminal, so the guard
    /// has to travel inside the argument vector instead.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public static IReadOnlyList<string> BuildGuardedCommandLine(
        ServerGeneration expected,
        string mismatchCommand,
        params IReadOnlyList<string>[] commands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mismatchCommand);
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Length == 0)
        {
            throw new ArgumentException("At least one command is required.", nameof(commands));
        }

        return TmuxCommandRequest.Group(
            [TmuxGenerationGuard.Conditional(expected, mismatchCommand), .. commands]).EncodeArguments();
    }
}
