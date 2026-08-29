using System.Runtime.Versioning;

namespace LibTmux;

// Builds and executes window requests.
public static partial class TmuxChaining
{
    /// <summary>Returns a window request as one tmux command.</summary>
    /// <param name="request">The window to create.</param>
    /// <param name="session">The session the window is created in.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this NewWindowRequest request, Session session)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        return Command([.. Session.BuildNewWindowArguments(request, session.Id.ToString())]) with
        {
            RequiredGeneration = session.Generation,
        };
    }

    /// <summary>Returns a layout request as one tmux command for a window.</summary>
    /// <param name="request">The layout to apply.</param>
    /// <param name="window">The window the layout applies to.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// This takes the window because a layout name is checked against the ones
    /// the running tmux knows, and an unrecognised name takes the whole server
    /// down on tmux 3.3a. Batching a layout must not skip that check.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxWindowException">The layout is one tmux may not recognise.</exception>
    public static TmuxCommand ToCommand(this SelectLayoutRequest request, Window window)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(window);
        return Command([.. window.BuildSelectLayoutArguments(request)]);
    }

    /// <summary>Runs a layout request on its own.</summary>
    /// <param name="request">The layout to apply.</param>
    /// <param name="window">The window the layout applies to.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary layout.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this SelectLayoutRequest request,
        Window window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Server
            .Chain()
            .Then(request.ToCommand(window))
            .ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a window-resize request as one tmux command.</summary>
    /// <param name="request">The size to apply.</param>
    /// <param name="window">The window being resized.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this ResizeWindowRequest request, Window window)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(window);
        return Command([.. window.BuildResizeWindowArguments(request)]);
    }

    /// <summary>Runs a window-resize request on its own.</summary>
    /// <param name="request">The size to apply.</param>
    /// <param name="window">The window being resized.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary resize.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ResizeWindowRequest request,
        Window window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Server.Chain().Then(request.ToCommand(window)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a link request as one tmux command.</summary>
    /// <param name="request">Where the link goes.</param>
    /// <param name="window">The window being linked.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// This takes the window because the link's source is the session that
    /// window was read through, which a window resolved by identifier alone
    /// does not know.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier, so its source link is unknown.
    /// </exception>
    public static TmuxCommand ToCommand(this LinkWindowRequest request, Window window)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(window);
        return Command([.. window.BuildLinkWindowArguments(request)]);
    }

    /// <summary>Runs a link request on its own.</summary>
    /// <param name="request">Where the link goes.</param>
    /// <param name="window">The window being linked.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary link.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this LinkWindowRequest request,
        Window window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Server.Chain().Then(request.ToCommand(window)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a window-move request as one tmux command.</summary>
    /// <param name="request">Where the window goes.</param>
    /// <param name="window">The window being moved.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this MoveWindowRequest request, Window window)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(window);
        return Command([.. window.BuildMoveWindowArguments(request)]);
    }

    /// <summary>Runs a window-move request on its own.</summary>
    /// <param name="request">Where the window goes.</param>
    /// <param name="window">The window being moved.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary move.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this MoveWindowRequest request,
        Window window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Server.Chain().Then(request.ToCommand(window)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Runs a window request on its own.</summary>
    /// <param name="request">The window to create.</param>
    /// <param name="session">The session that will hold it.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which names the created window.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this NewWindowRequest request,
        Session session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Server
            .Chain()
            .Then(request.ToCommand(session))
            .ExecuteAsync(cancellationToken);
    }
}
