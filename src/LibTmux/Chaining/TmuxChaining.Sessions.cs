using System.Runtime.Versioning;

namespace LibTmux;

// Builds and executes session requests.
public static partial class TmuxChaining
{
    /// <summary>Returns a session request as one tmux command.</summary>
    /// <param name="request">The session to create.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    public static TmuxCommand ToCommand(this NewSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Command([.. Server.BuildNewSessionArguments(request)]);
    }

    /// <summary>Returns an attach request as one tmux command.</summary>
    /// <param name="request">How to attach.</param>
    /// <param name="session">The session being attached to.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this AttachSessionRequest request, Session session)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        return Command([.. Session.BuildAttachArguments(request, session.Id.ToString())]) with
        {
            RequiredGeneration = session.Generation,
        };
    }

    /// <summary>Runs an attach request on its own.</summary>
    /// <param name="request">How to attach.</param>
    /// <param name="session">The session being attached to.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed.</returns>
    /// <remarks>
    /// Attaching needs a terminal, so this fails from a process that has none.
    /// It is here because a chain that switches a client between sessions is
    /// built the same way.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this AttachSessionRequest request,
        Session session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Server
            .Chain()
            .Then(request.ToCommand(session))
            .ExecuteAsync(cancellationToken);
    }

    /// <summary>Runs a session request on its own.</summary>
    /// <param name="request">The session to create.</param>
    /// <param name="server">The server to create it on.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which names the created session.</returns>
    /// <remarks>
    /// This runs the same command <see cref="ToCommand(NewSessionRequest)" />
    /// builds, so a request executed on its own and the same request added to
    /// a chain do the same thing. Reach for the chain when there is more than
    /// one command; a single request costs the same either way.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this NewSessionRequest request,
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Chain().Then(request.ToCommand()).ExecuteAsync(cancellationToken);
    }
}
