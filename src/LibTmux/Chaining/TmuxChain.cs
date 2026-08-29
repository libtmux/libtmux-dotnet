using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>Commands tmux runs together, in one process.</summary>
/// <remarks>
/// <para>
/// A one-shot call starts a tmux client, runs one command, and lets it exit,
/// which is the right shape for one command and the wrong shape for fifty: the
/// process start dominates. A chain hands tmux the whole sequence at once, so
/// the cost is paid once no matter how many commands are in it.
/// </para>
/// <para>
/// Building a chain reaches nothing; only <see cref="ExecuteAsync" /> does.
/// Each step returns a new chain, so a partly built one can be shared without
/// another caller's additions appearing in it.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class TmuxChain
{
    private readonly TmuxCommandDispatcher _dispatcher;
    private readonly IReadOnlyList<TmuxCommand> _commands;
    private readonly Func<ServerGeneration, IReadOnlyList<IReadOnlyList<string>>,
        CancellationToken, Task<TmuxCommandResult>>? _guarded;

    internal TmuxChain(
        TmuxCommandDispatcher dispatcher,
        IReadOnlyList<TmuxCommand> commands,
        Func<ServerGeneration, IReadOnlyList<IReadOnlyList<string>>,
            CancellationToken, Task<TmuxCommandResult>>? guarded = null)
    {
        _dispatcher = dispatcher;
        _commands = commands;
        _guarded = guarded;
    }

    /// <summary>Gets the commands this chain will run, in order.</summary>
    public IReadOnlyList<TmuxCommand> Commands => _commands;

    /// <summary>Adds one command and returns the longer chain.</summary>
    /// <param name="command">The command to run after the ones already added.</param>
    /// <returns>A chain ending with <paramref name="command" />.</returns>
    public TmuxChain Then(TmuxCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new TmuxChain(_dispatcher, [.. _commands, command], _guarded);
    }

    /// <summary>Adds every command in order and returns the longer chain.</summary>
    /// <param name="commands">The commands to run, in order, after the ones already added.</param>
    /// <returns>A chain ending with <paramref name="commands" />.</returns>
    /// <remarks>
    /// One request can answer several commands. This takes what
    /// <see cref="TmuxChaining.ToCommands(SetHooksRequest, TmuxHooks)" /> returns
    /// without unrolling it at the call site.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    public TmuxChain Then(IEnumerable<TmuxCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        TmuxCommand[] added = [.. commands];
        if (Array.IndexOf(added, null) >= 0)
        {
            throw new ArgumentException("A chained command cannot be null.", nameof(commands));
        }

        return new TmuxChain(_dispatcher, [.. _commands, .. added], _guarded);
    }

    /// <summary>Adds one command by name and returns the longer chain.</summary>
    /// <param name="name">The tmux command name.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>A chain ending with the named command.</returns>
    public TmuxChain Then(string name, params string[] arguments) =>
        Then(TmuxCommand.Create(name, arguments));

    /// <summary>Runs every command in one tmux invocation.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What that one invocation produced.</returns>
    /// <remarks>
    /// tmux runs the commands in order and prints their output as one stream,
    /// so the result belongs to the chain rather than to any single command.
    /// A command that fails stops the ones after it, which is tmux's own
    /// behavior for a grouped run rather than anything imposed here.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The chain has no commands.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the run failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<TmuxCommandResult> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        if (_commands.Count == 0)
        {
            throw new InvalidOperationException("A chain needs at least one command.");
        }

        // Commands must share one server generation, checked once per batch:
        // a per-command check could race with a server change between them.
        ServerGeneration[] required = [.. _commands
            .Select(static command => command.RequiredGeneration)
            .Where(static generation => generation.HasValue)
            .Select(static generation => generation!.Value)
            .Distinct()];

        if (required.Length > 1)
        {
            throw new InvalidOperationException(
                "A chain mixes commands from different server generations, which "
                + "cannot all be valid: at most one of those servers is running.");
        }

        IReadOnlyList<IReadOnlyList<string>> arguments =
            [.. _commands.Select(static command => command.ToArguments())];

        TmuxCommandResult result = required.Length == 1 && _guarded is not null
            ? await _guarded(required[0], arguments, cancellationToken).ConfigureAwait(false)
            : await _dispatcher.ExecuteGroupAsync(arguments, cancellationToken)
                .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "chain");
        return result;
    }
}
