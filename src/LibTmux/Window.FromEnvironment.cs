using System.Runtime.Versioning;

namespace LibTmux;

// Resolves a window from tmux's exported environment.
public sealed partial class Window
{
    /// <summary>Returns the window holding the pane this process runs in.</summary>
    /// <param name="environment">The environment, or null for the process.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The resolved window.</returns>
    /// <remarks>
    /// Resolved through the pane rather than through the session id in
    /// <c>TMUX</c>, which is frozen at pane spawn and goes stale when the
    /// window is moved.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public static async Task<Window> FromEnvironmentAsync(
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        Pane pane = await Pane.FromEnvironmentAsync(environment, cancellationToken)
            .ConfigureAwait(false);
        return pane.Window;
    }
}
