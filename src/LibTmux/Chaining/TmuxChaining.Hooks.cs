using System.Runtime.Versioning;

namespace LibTmux;

// Builds and executes hook requests.
public static partial class TmuxChaining
{
    /// <summary>Returns a hook request as one tmux command.</summary>
    /// <param name="request">Which hook to set, and to what.</param>
    /// <param name="hooks">The hooks handle whose scope the hook is set in.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public static TmuxCommand ToCommand(this SetHookRequest request, TmuxHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hooks);
        return Command([.. hooks.BuildSetArguments(request)]) with
        {
            RequiredGeneration = hooks.Generation,
        };
    }

    /// <summary>Runs a hook request on its own.</summary>
    /// <param name="request">Which hook to set, and to what.</param>
    /// <param name="hooks">The hooks handle whose scope the hook is set in.</param>
    /// <param name="server">The server the hook is set on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary hook.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this SetHookRequest request,
        TmuxHooks hooks,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(hooks)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a hook listing as one tmux command.</summary>
    /// <param name="request">Which scope to list.</param>
    /// <param name="hooks">The hooks handle whose scope is listed.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// A chain returns one combined output stream, so batch a listing to see
    /// what the same invocation just installed. <see cref="TmuxHooks.GetAllAsync" />
    /// answers the same question with the hooks already parsed.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public static TmuxCommand ToCommand(this ListHooksRequest request, TmuxHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hooks);
        return Command([.. hooks.BuildListArguments(request)]) with
        {
            RequiredGeneration = hooks.Generation,
        };
    }

    /// <summary>Runs a hook listing on its own.</summary>
    /// <param name="request">Which scope to list.</param>
    /// <param name="hooks">The hooks handle whose scope is listed.</param>
    /// <param name="server">The server the hooks are read from.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, unparsed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ListHooksRequest request,
        TmuxHooks hooks,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(hooks)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns running a hook as one tmux command.</summary>
    /// <param name="request">Which hook to run.</param>
    /// <param name="hooks">The hooks handle whose scope holds it.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// A hook request names a hook without saying what to do with it, so
    /// running and removing are separate here rather than one call that has to
    /// guess which was meant.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public static TmuxCommand ToRunCommand(this HookRequest request, TmuxHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hooks);
        return Command([.. hooks.BuildRunArguments(request)]) with
        {
            RequiredGeneration = hooks.Generation,
        };
    }

    /// <summary>Returns removing a hook as one tmux command.</summary>
    /// <param name="request">Which hook to remove.</param>
    /// <param name="hooks">The hooks handle whose scope holds it.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public static TmuxCommand ToUnsetCommand(this HookRequest request, TmuxHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hooks);
        return Command([.. hooks.BuildUnsetArguments(request)]) with
        {
            RequiredGeneration = hooks.Generation,
        };
    }

    /// <summary>Runs a hook on its own.</summary>
    /// <param name="request">Which hook to run.</param>
    /// <param name="hooks">The hooks handle whose scope holds it.</param>
    /// <param name="server">The server the hook runs on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary run.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this HookRequest request,
        TmuxHooks hooks,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToRunCommand(hooks)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns every command a multi-entry hook request sends.</summary>
    /// <param name="request">Which hook to set, and to what entries.</param>
    /// <param name="hooks">The hooks handle whose scope holds it.</param>
    /// <returns>The commands, in the order tmux must receive them.</returns>
    /// <remarks>
    /// This request is several tmux commands rather than one, so it answers a
    /// list. Running them one at a time is what the one-shot path does; adding
    /// them to a chain is what this is for.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public static IReadOnlyList<TmuxCommand> ToCommands(
        this SetHooksRequest request,
        TmuxHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hooks);
        return
        [
            .. hooks.BuildSetAllArguments(request)
                .Select(arguments => Command([.. arguments]) with
                {
                    RequiredGeneration = hooks.Generation,
                }),
        ];
    }

    /// <summary>Runs a multi-entry hook request in one invocation.</summary>
    /// <param name="request">Which hook to set, and to what entries.</param>
    /// <param name="hooks">The hooks handle whose scope holds it.</param>
    /// <param name="server">The server the hook is set on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What that one invocation produced.</returns>
    /// <remarks>
    /// The one-shot path sends these one process at a time, so this is the
    /// case batching helps most.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the run failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this SetHooksRequest request,
        TmuxHooks hooks,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommands(hooks)).ExecuteAsync(cancellationToken);
    }
}
