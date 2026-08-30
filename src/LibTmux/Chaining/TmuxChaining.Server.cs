using System.Runtime.Versioning;

namespace LibTmux;

// Builds and executes server-scoped requests.
public static partial class TmuxChaining
{
    /// <summary>Returns a conditional request as one tmux command.</summary>
    /// <param name="request">What to run.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    public static TmuxCommand ToCommand(this IfShellRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Command([.. Server.BuildIfShellArguments(request)]);
    }

    /// <summary>Runs a conditional request on its own.</summary>
    /// <param name="request">What to run.</param>
    /// <param name="server">The server to run it on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this IfShellRequest request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand()).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a channel request as one tmux command.</summary>
    /// <param name="request">What to run.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    public static TmuxCommand ToCommand(this WaitForRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Command([.. Server.BuildWaitForArguments(request)]);
    }

    /// <summary>Runs a channel request on its own.</summary>
    /// <param name="request">What to run.</param>
    /// <param name="server">The server to run it on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this WaitForRequest request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand()).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a message request as one tmux command.</summary>
    /// <param name="request">What to show, and where.</param>
    /// <param name="server">The server the message is shown on.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// This takes the server because two of the flags depend on which tmux is
    /// answering: literal expansion arrived in 3.4, and 3.2a refuses the
    /// target-client flag even for a client that is really attached.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this DisplayMessageRequest request, Server server)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(server);
        return Command([.. server.BuildDisplayMessageArguments(request)]);
    }

    /// <summary>Runs a message request on its own.</summary>
    /// <param name="request">What to show, and where.</param>
    /// <param name="server">The server the message is shown on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is the message when it was asked for.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this DisplayMessageRequest request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(server)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a shell request as one tmux command.</summary>
    /// <param name="request">What to run, and how.</param>
    /// <param name="server">The server that runs it.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Three of this command's flags arrived at different tmux versions, so
    /// the server is what decides which of them the built command carries.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this RunShellRequest request, Server server)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(server);
        return Command([.. server.BuildRunShellArguments(request)]);
    }

    /// <summary>Runs a shell request on its own.</summary>
    /// <param name="request">What to run, and how.</param>
    /// <param name="server">The server that runs it.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is the command's output.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this RunShellRequest request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(server)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a menu request as one tmux command.</summary>
    /// <param name="request">What the menu offers, and how it looks.</param>
    /// <param name="server">The server the menu is shown on.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// The style flags arrived in tmux 3.4 and the mouse flag in 3.5, so the
    /// server decides which of them the built command carries.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this DisplayMenuRequest request, Server server)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(server);
        return Command([.. server.BuildDisplayMenuArguments(request)]);
    }

    /// <summary>Runs a menu request on its own.</summary>
    /// <param name="request">What the menu offers, and how it looks.</param>
    /// <param name="server">The server the menu is shown on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary menu.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this DisplayMenuRequest request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(server)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a confirmation request as one tmux command.</summary>
    /// <param name="request">What to confirm, and what to run when it is.</param>
    /// <param name="server">The server the confirmation is shown on.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Naming the accepting key, and defaulting to yes, arrived in tmux 3.4,
    /// so the server decides whether the built command carries them.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public static TmuxCommand ToCommand(this ConfirmBeforeRequest request, Server server)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(server);
        return Command([.. server.BuildConfirmBeforeArguments(request)]);
    }

    /// <summary>Runs a confirmation request on its own.</summary>
    /// <param name="request">What to confirm, and what to run when it is.</param>
    /// <param name="server">The server the confirmation is shown on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary confirmation.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ConfirmBeforeRequest request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(server)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a prompt request as one tmux command.</summary>
    /// <param name="request">What to ask, and how.</param>
    /// <param name="server">The server the prompt is shown on.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Batching does not soften the refusal below tmux 3.3: that version reads
    /// the type flag as something else, so a prompt asking for one is refused
    /// here exactly as it is when run alone.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxVersionTooLowException">
    /// The request asks for a format or a prompt type and tmux is older than 3.3.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public static TmuxCommand ToCommand(this CommandPromptRequest request, Server server)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(server);
        return Command([.. server.BuildCommandPromptArguments(request)]);
    }

    /// <summary>Runs a prompt request on its own.</summary>
    /// <param name="request">What to ask, and how.</param>
    /// <param name="server">The server the prompt is shown on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary prompt.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this CommandPromptRequest request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(server)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns an access request as one tmux command.</summary>
    /// <param name="request">Whose access to change, and how.</param>
    /// <param name="server">The server whose access is changed.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// The command itself arrived in tmux 3.3, so batching does not soften the
    /// refusal below that: an older server has nothing to send it to.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxVersionTooLowException">tmux is older than 3.3.</exception>
    [UnsupportedOSPlatform("windows")]
    public static TmuxCommand ToCommand(this ServerAccessRequest request, Server server)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(server);
        return Command([.. server.BuildServerAccessArguments(request)]);
    }

    /// <summary>Runs an access request on its own.</summary>
    /// <param name="request">Whose access to change, and how.</param>
    /// <param name="server">The server whose access is changed.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which lists the users when it was asked to.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ServerAccessRequest request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand(server)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a buffer-listing request as one tmux command.</summary>
    /// <param name="request">How the buffers are rendered and filtered.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    public static TmuxCommand ToCommand(this ListBuffersRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Command([.. Server.BuildListBuffersArguments(request)]);
    }

    /// <summary>Runs a buffer-listing request on its own.</summary>
    /// <param name="request">How the buffers are rendered and filtered.</param>
    /// <param name="server">The server whose buffers are listed.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is one line per buffer.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ListBuffersRequest request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand()).ExecuteAsync(cancellationToken);
    }
}
