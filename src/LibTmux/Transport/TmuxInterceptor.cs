namespace LibTmux;

/// <summary>Wraps one tmux invocation: observe it, retry it, refuse it, or answer it.</summary>
/// <param name="invocation">What tmux is about to be asked.</param>
/// <param name="next">
/// Runs the invocation against tmux. Call it once to pass through, again to
/// retry, or not at all to refuse or to answer in tmux's place.
/// </param>
/// <param name="cancellationToken">
/// The command's token, already bounded by
/// <see cref="ServerConnectionOptions.CommandTimeout" /> when one is set.
/// </param>
/// <returns>tmux's answer, or one the interceptor made instead.</returns>
public delegate Task<TmuxCommandResult> TmuxInterceptor(
    TmuxInvocation invocation,
    Func<CancellationToken, Task<TmuxCommandResult>> next,
    CancellationToken cancellationToken);

/// <summary>One tmux invocation, as a <see cref="TmuxInterceptor" /> sees it.</summary>
public sealed class TmuxInvocation
{
    /// <summary>Initializes an invocation.</summary>
    /// <param name="arguments">The arguments tmux receives.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arguments" /> is null.</exception>
    public TmuxInvocation(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        Arguments = [.. arguments];
    }

    /// <summary>Gets the arguments tmux receives.</summary>
    /// <remarks>
    /// These follow the socket and configuration flags, which the connection
    /// adds itself, and match <see cref="TmuxCommandResult.Arguments" /> on a
    /// result tmux produced. Grouped commands are separated by a <c>;</c>
    /// argument.
    /// </remarks>
    public IReadOnlyList<string> Arguments { get; }
}
