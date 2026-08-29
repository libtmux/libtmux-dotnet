using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Describes one <c>respawn-window</c> or <c>respawn-pane</c> invocation.</summary>
/// <remarks>
/// tmux refuses to respawn anything still running, so a caller who means to
/// restart a live window or pane has to say so with
/// <see cref="KillExistingProcess" />.
/// </remarks>
public sealed record RespawnRequest
{
    /// <summary>Initializes a respawn request.</summary>
    /// <param name="command">The command to run, or null to reuse the original.</param>
    /// <param name="startDirectory">The working directory to respawn in.</param>
    /// <param name="environment">Environment entries set on the respawned target.</param>
    /// <param name="killExistingProcess">Whether a running process is killed first.</param>
    public RespawnRequest(
        string? command = null,
        string? startDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool killExistingProcess = false)
    {
        Command = command;
        StartDirectory = startDirectory;
        // The request is read again at dispatch, so a caller that kept the
        // dictionary could otherwise change the argv after constructing it.
        Environment = environment is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(environment, StringComparer.Ordinal));
        KillExistingProcess = killExistingProcess;
    }

    /// <summary>Gets the command to run, or null to reuse the original.</summary>
    public string? Command { get; }

    /// <summary>Gets the working directory to respawn in.</summary>
    /// <remarks>
    /// tmux expands it as a format before it changes directory, so a <c>#</c>
    /// in it does not survive verbatim.
    /// </remarks>
    public string? StartDirectory { get; }

    /// <summary>Gets the environment entries set on the respawned target.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; }

    /// <summary>Gets whether a running process is killed first.</summary>
    /// <remarks>Respawning a live target fails without this.</remarks>
    public bool KillExistingProcess { get; }
}
