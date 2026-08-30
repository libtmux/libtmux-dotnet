using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Starts a chain of commands on this server.
public sealed partial class Server
{
    /// <summary>Begins a chain that runs its commands in one tmux invocation.</summary>
    /// <returns>An empty chain bound to this server.</returns>
    /// <remarks>
    /// This is the batched counterpart to the one-shot methods. Which one is
    /// in use is visible where the call starts rather than in an option, and
    /// nothing runs until the chain is executed.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The handle has no connection.</exception>
    [UnsupportedOSPlatform("windows")]
    public TmuxChain Chain()
    {
        TmuxConnection connection = _connection
            ?? throw new InvalidOperationException("The server handle has no connection.");
        // Starts with no generation guard. A command supplies one when it
        // carries an identifier this library read from a handle -- a pane, a
        // window, a session, or one of their option and hook tables -- because
        // tmux gives that identifier to something else after a restart. A
        // server-wide command names no such identifier and needs none.
        return new TmuxChain(
            connection.ServerDispatcher,
            [],
            connection.ExecuteGuardedGroupAsync);
    }
}
