namespace LibTmux;

/// <summary>Describes one <c>pipe-pane</c> invocation.</summary>
public sealed record PipePaneRequest : ITmuxRequest<Pane>
{
    /// <summary>Gets the command to pipe through, or null to stop piping.</summary>
    /// <remarks>
    /// Omitting a command does not leave an existing pipe alone: it stops it.
    /// </remarks>
    public string? Command { get; init; }

    /// <summary>Gets whether only pane output is piped.</summary>
    public bool OutputOnly { get; init; }

    /// <summary>Gets whether only pane input is piped.</summary>
    public bool InputOnly { get; init; }

    /// <summary>Gets whether an identical existing pipe is stopped instead.</summary>
    public bool Toggle { get; init; }

    /// <summary>Returns a pane-piping request as one tmux command.</summary>
    /// <param name="pane">The pane being piped.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pane" /> is null.</exception>
    public TmuxCommand ToCommand(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TmuxChaining.Command([.. pane.BuildPipePaneArguments(this)]) with
        {
            RequiredGeneration = pane.Generation,
        };
    }
}
