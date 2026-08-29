using System.Runtime.Versioning;

using LibTmux.Internal;

namespace LibTmux;

// Resolves a pane from tmux's exported environment.
public sealed partial class Pane
{
    /// <summary>Returns the pane this process was spawned in.</summary>
    /// <param name="environment">The environment, or null for the process.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The resolved pane.</returns>
    /// <exception cref="TmuxObjectNotFoundException">
    /// The environment does not name a pane, or tmux no longer has it.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public static async Task<Pane> FromEnvironmentAsync(
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        if (!TmuxEnvironmentVariables.TryReadPane(environment, out PaneId id))
        {
            throw new TmuxObjectNotFoundException(
                "The environment does not name a tmux pane.",
                TmuxEnvironmentVariables.PaneVariable);
        }

        Server server = await Server.FromEnvironment(environment)
            .ConnectAsync(cancellationToken)
            .ConfigureAwait(false);
        // Materialize rather than resolve by identifier: callers reach for
        // Session and Window straight off this pane, and those relations are
        // served from the captured snapshot.
        IReadOnlyDictionary<string, string?> row = await RelationReader
            .FindAsync(
                server,
                "list-panes",
                "pane_id",
                id.ToString(),
                inSession: null,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TmuxObjectNotFoundException(
                $"tmux no longer has pane '{id}'.",
                id.ToString());
        return RelationReader.ToPane(server, row);
    }
}
