using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Describes one <c>respawn-window</c> or <c>respawn-pane</c> invocation.</summary>
/// <remarks>
/// tmux refuses to respawn anything still running, so a caller who means to
/// restart a live window or pane has to say so with
/// <see cref="KillExistingProcess" />.
/// </remarks>
public sealed record RespawnRequest : ITmuxRequest<Pane>
{
    private readonly IReadOnlyDictionary<string, string>? _environment;

    /// <summary>Gets the command to run, or null to reuse the original.</summary>
    public string? Command { get; init; }

    /// <summary>Gets the working directory to respawn in.</summary>
    /// <remarks>
    /// tmux expands it as a format before it changes directory, so a <c>#</c>
    /// in it does not survive verbatim.
    /// </remarks>
    public string? StartDirectory { get; init; }

    /// <summary>Gets the environment entries set on the respawned target.</summary>
    public IReadOnlyDictionary<string, string>? Environment
    {
        get => _environment;

        // The request is read again at dispatch, so a caller that kept the
        // dictionary could otherwise change the argv after building it.
        init => _environment = value is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(value, StringComparer.Ordinal));
    }

    /// <summary>Gets whether a running process is killed first.</summary>
    /// <remarks>Respawning a live target fails without this.</remarks>
    public bool KillExistingProcess { get; init; }

    /// <summary>Returns a respawn request as one tmux command for a pane.</summary>
    /// <param name="pane">The pane being respawned.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pane" /> is null.</exception>
    public TmuxCommand ToCommand(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TmuxChaining.Command([.. pane.BuildRespawnPaneArguments(this)]) with
        {
            RequiredGeneration = pane.Generation,
        };
    }
}
