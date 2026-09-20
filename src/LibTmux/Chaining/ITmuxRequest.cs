namespace LibTmux;

/// <summary>A request that becomes one tmux command against a target.</summary>
/// <typeparam name="TTarget">
/// What the command is aimed at: a <see cref="Pane" />, <see cref="Window" />,
/// <see cref="Session" />, <see cref="Server" />, or a <see cref="TmuxOptions" />
/// or <see cref="TmuxHooks" /> table.
/// </typeparam>
/// <remarks>
/// <para>
/// Every request builds its command with the same code the one-shot method
/// uses, so a chained call and a direct call send identical arguments rather
/// than two descriptions that have to be kept in step.
/// </para>
/// <para>
/// What each request is aimed at is decided by what tmux needs, not by taste.
/// A window names the session that will hold it; keys name the pane, because
/// which flags tmux accepts depends on the server version and the pane is what
/// knows it. A request that names nothing in particular, such as a new
/// session, is aimed at the server that runs it.
/// </para>
/// <para>
/// <see cref="TmuxChaining" /> runs any of these on its own; a
/// <see cref="TmuxChain" /> runs several in one tmux invocation.
/// </para>
/// </remarks>
public interface ITmuxRequest<TTarget>
    where TTarget : class
{
    /// <summary>Returns this request as one tmux command.</summary>
    /// <param name="target">What the command is aimed at.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    public TmuxCommand ToCommand(TTarget target);
}
