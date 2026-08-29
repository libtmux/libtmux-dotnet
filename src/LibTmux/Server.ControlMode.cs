using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Opens a live control client on this server.
public sealed partial class Server
{
    /// <summary>Starts a tmux control client and keeps it running.</summary>
    /// <param name="target">
    /// The session to attach to, or <see langword="null" /> to let tmux pick
    /// the most recently used one.
    /// </param>
    /// <param name="cancellationToken">Cancels starting the client.</param>
    /// <returns>A session that reports what tmux does until it is disposed.</returns>
    /// <remarks>
    /// This is the streaming counterpart to the one-shot methods, not a mode
    /// they can be switched into: the rest of this type starts a client, runs
    /// one command, and lets it exit, which is why it never sees anything it
    /// did not ask for. Hold the returned session and read
    /// <see cref="IControlModeSession.Events" /> to see the rest.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The handle has no connection.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<IControlModeSession> EnterControlModeAsync(
        string? target = null,
        CancellationToken cancellationToken = default)
    {
        TmuxConnection connection = _connection
            ?? throw new InvalidOperationException("The server handle has no connection.");

        // Attaching needs a session to attach to, and a server with none exits
        // the moment it is started. Discovering first turns "no server" into
        // the ordinary connection error rather than a client that dies at once.
        Server live = await RediscoverCurrentGenerationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (connection.IsPsmux)
        {
            throw new NotSupportedException(
                "psmux control mode does not provide the attach readiness framing LibTmux requires.");
        }

        ControlModeSession session = ControlModeSession.Start(
            connection.Options.TmuxBinaryPath,
            connection.PrefixArguments,
            target,
            live.Generation!.Value,
            startInfo => TmuxConnection.ApplyChildEnvironment(
                startInfo,
                connection.Options.ChildEnvironment));

        // Attaching is asynchronous, and a caller who sends a command before
        // tmux has answered its own attach would be handed that answer.
        try
        {
            await session.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch (Exception startupFailure)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                startupFailure.Data["LibTmux.ControlModeCleanupFailure"] = cleanupFailure;
            }

            throw;
        }
    }
}
