using System.Runtime.Versioning;

namespace LibTmux;

// Builds and executes option requests.
public static partial class TmuxChaining
{
    /// <summary>Returns an option request as one tmux command.</summary>
    /// <param name="request">Which option to set, and to what.</param>
    /// <param name="options">The options handle whose scope the option is set in.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// This takes the options handle rather than a server, because which
    /// scope flags and target tmux receives follow from the handle the caller
    /// reached for: a window's options and a server's are the same request
    /// spelled differently.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public static TmuxCommand ToCommand(this SetOptionRequest request, TmuxOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        return Command([.. options.BuildSetArguments(request)]);
    }

    /// <summary>Runs an option request on its own.</summary>
    /// <param name="request">Which option to set, and to what.</param>
    /// <param name="options">The options handle whose scope the option is set in.</param>
    /// <param name="server">The server the option is set on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary set.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this SetOptionRequest request,
        TmuxOptions options,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(options)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns an unset request as one tmux command.</summary>
    /// <param name="request">Which option to unset, and how.</param>
    /// <param name="options">The options handle whose scope the option is unset in.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public static TmuxCommand ToCommand(this UnsetOptionRequest request, TmuxOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        return Command([.. options.BuildUnsetArguments(request)]);
    }

    /// <summary>Runs an unset request on its own.</summary>
    /// <param name="request">Which option to unset, and how.</param>
    /// <param name="options">The options handle whose scope the option is unset in.</param>
    /// <param name="server">The server the option is unset on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary unset.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this UnsetOptionRequest request,
        TmuxOptions options,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(options)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a named option read as one tmux command.</summary>
    /// <param name="request">Which option to read.</param>
    /// <param name="options">The options handle whose scope is read.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// A chain returns one combined output stream, so several reads batched
    /// together arrive undelimited. Reach for this to read something beside
    /// the changes a chain makes; reach for the handle's own accessor when
    /// what you want is a parsed value.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public static TmuxCommand ToCommand(this GetOptionRequest request, TmuxOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        return Command([.. options.BuildGetArguments(request)]);
    }

    /// <summary>Runs a named option read on its own.</summary>
    /// <param name="request">Which option to read.</param>
    /// <param name="options">The options handle whose scope is read.</param>
    /// <param name="server">The server the option is read from.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, unparsed.</returns>
    /// <remarks>
    /// <see cref="TmuxOptions.GetAsync" /> answers the same question with the
    /// value already parsed, and is what most callers want.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this GetOptionRequest request,
        TmuxOptions options,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(options)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a whole-scope option read as one tmux command.</summary>
    /// <param name="request">How the scope is read.</param>
    /// <param name="options">The options handle whose scope is read.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public static TmuxCommand ToCommand(this GetOptionsRequest request, TmuxOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        return Command([.. options.BuildGetAllArguments(request)]);
    }

    /// <summary>Runs a whole-scope option read on its own.</summary>
    /// <param name="request">How the scope is read.</param>
    /// <param name="options">The options handle whose scope is read.</param>
    /// <param name="server">The server the options are read from.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, unparsed.</returns>
    /// <remarks>
    /// <see cref="TmuxOptions.GetAllAsync" /> answers the same question with
    /// the values already parsed, and is what most callers want.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this GetOptionsRequest request,
        TmuxOptions options,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(options)).ExecuteAsync(cancellationToken);
    }
}
