using System.Runtime.Versioning;

namespace LibTmux;

// Resolves a session from tmux's exported environment.
public sealed partial class Session
{
    /// <summary>Returns the session holding the pane this process runs in.</summary>
    /// <param name="environment">The environment, or null for the process.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The resolved session.</returns>
    /// <remarks>
    /// Resolved through the pane rather than through the session id in
    /// <c>TMUX</c>. That id is frozen at pane spawn, so it names the wrong
    /// session once the pane's window is moved or linked elsewhere.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public static async Task<Session> FromEnvironmentAsync(
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        Pane pane = await Pane.FromEnvironmentAsync(environment, cancellationToken)
            .ConfigureAwait(false);
        return pane.Session;
    }
}
