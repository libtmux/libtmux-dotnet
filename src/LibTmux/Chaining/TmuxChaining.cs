using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>Runs a request on its own, as a chain of one command.</summary>
/// <remarks>
/// There is one overload per kind of target rather than one per request: a
/// request says what it is aimed at through <see cref="ITmuxRequest{TTarget}" />,
/// and the target says which server runs it. An options or hooks table does not
/// know its server, so those two overloads also take the server.
/// </remarks>
public static partial class TmuxChaining
{
    /// <summary>Runs a pane request on its own.</summary>
    /// <param name="request">What to do.</param>
    /// <param name="pane">The pane it is aimed at.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ITmuxRequest<Pane> request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Runs a window request on its own.</summary>
    /// <param name="request">What to do.</param>
    /// <param name="window">The window it is aimed at.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ITmuxRequest<Window> request,
        Window window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(window);
        return window.Server.Chain().Then(request.ToCommand(window)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Runs a session request on its own.</summary>
    /// <param name="request">What to do.</param>
    /// <param name="session">The session it is aimed at.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ITmuxRequest<Session> request,
        Session session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        return session.Server.Chain().Then(request.ToCommand(session)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Runs a server request on its own.</summary>
    /// <param name="request">What to do.</param>
    /// <param name="server">The server that runs it.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ITmuxRequest<Server> request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(server)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Runs an option request on its own.</summary>
    /// <param name="request">What to read or write.</param>
    /// <param name="options">The options table whose scope it is aimed at.</param>
    /// <param name="server">The server that holds the table.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ITmuxRequest<TmuxOptions> request,
        TmuxOptions options,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(options)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Runs a hook request on its own.</summary>
    /// <param name="request">What to read or write.</param>
    /// <param name="hooks">The hooks table whose scope it is aimed at.</param>
    /// <param name="server">The server that holds the table.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ITmuxRequest<TmuxHooks> request,
        TmuxHooks hooks,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(hooks)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Runs the text and optional Enter commands of a key request.</summary>
    /// <param name="request">The keys to send.</param>
    /// <param name="pane">The pane that receives the keys.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The combined command result.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The request sends nothing or its text contains NUL.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this SendKeysRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommands(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Runs a key request through a control client.</summary>
    /// <param name="request">The keys to send, including a requested Enter.</param>
    /// <param name="pane">The pane that receives them.</param>
    /// <param name="control">The control client connected to the pane's server.</param>
    /// <param name="cancellationToken">Stops waiting for command replies.</param>
    /// <returns>The reply lines concatenated in command order.</returns>
    /// <remarks>
    /// Text and Enter run as separate requests. Other callers can send commands
    /// between them. Cancelling a wait does not undo input already sent.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The request sends nothing.</exception>
    /// <exception cref="ControlModeCommandException">The first command failed.</exception>
    /// <exception cref="LibTmuxException">
    /// Text succeeded but Enter failed or its wait was cancelled. Dispatch is
    /// unknown; do not retry the whole request.
    /// </exception>
    public static async Task<IReadOnlyList<string>> ExecuteAsync(
        this SendKeysRequest request,
        Pane pane,
        IControlModeSession control,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(control);
        IReadOnlyList<TmuxCommand> commands = request.ToCommands(pane);
        var sequence = new TmuxMutationSequence();
        List<string> output = [];
        foreach (TmuxCommand command in commands)
        {
            IReadOnlyList<string> lines = await sequence.MutateAsync(
                    () => control.SendAsync(command, cancellationToken))
                .ConfigureAwait(false);
            output.AddRange(lines);
        }

        return output.AsReadOnly();
    }

    internal static TmuxCommand Command(string[] arguments) =>
        new(arguments[0], arguments[1..]);
}
