using System.Runtime.Versioning;

using LibTmux.Internal;

namespace LibTmux;

// Resolves a server from tmux's exported environment.
public sealed partial class Server
{
    /// <summary>Returns the server whose pane this process was spawned in.</summary>
    /// <param name="environment">The environment, or null for the process.</param>
    /// <returns>An unmaterialized handle for the exported socket.</returns>
    /// <exception cref="TmuxObjectNotFoundException">
    /// The environment does not name a tmux server.
    /// </exception>
    /// <remarks>
    /// Only the socket path is used. The pid and session id in <c>TMUX</c> are
    /// frozen at pane spawn, and the session id goes stale as soon as the
    /// pane's window moves between sessions. Costs no tmux subprocess.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public static Server FromEnvironment(
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (TmuxEnvironmentVariables.HasPsmuxMarker(environment)
            || TmuxEnvironmentVariables.LooksLikePsmuxServer(environment))
        {
            throw new TmuxObjectNotFoundException(
                "A psmux environment cannot select the audited executable safely; open it with explicit connection options.",
                TmuxEnvironmentVariables.ServerVariable);
        }

        if (!TmuxEnvironmentVariables.TryRead(environment, out TmuxServerLocation? entry))
        {
            throw new TmuxObjectNotFoundException(
                "The environment does not name a tmux server.",
                TmuxEnvironmentVariables.ServerVariable);
        }

        return Open(new ServerConnectionOptions(socketPath: entry.SocketPath));
    }
}
