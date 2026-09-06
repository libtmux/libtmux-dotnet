using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace LibTmux.Internal;

/// <summary>Where the <c>TMUX</c> variable says a server is.</summary>
/// <remarks>
/// tmux exports <c>TMUX</c> into every pane it spawns as
/// <c>"&lt;socket-path&gt;,&lt;server-pid&gt;,&lt;session-id&gt;"</c>. Only the
/// socket path stays true for the pane's lifetime: the pid and session id are
/// frozen at spawn, and the session id goes stale as soon as the pane's window
/// moves between sessions.
/// </remarks>
/// <param name="SocketPath">The server socket the pane is attached to.</param>
/// <param name="ServerProcessId">The server pid recorded at pane spawn.</param>
/// <param name="SessionId">The session id recorded at pane spawn.</param>
internal sealed record TmuxServerLocation(
    string SocketPath,
    int ServerProcessId,
    SessionId SessionId);

/// <summary>Reads the variables tmux exports into the panes it spawns.</summary>
internal static class TmuxEnvironmentVariables
{
    /// <summary>The variable tmux exports into every pane it spawns.</summary>
    internal const string ServerVariable = "TMUX";

    /// <summary>The variable naming the pane a process was spawned in.</summary>
    internal const string PaneVariable = "TMUX_PANE";

    /// <summary>The variable psmux exports with the current session name.</summary>
    internal const string PsmuxSessionVariable = "PSMUX_SESSION";

    /// <summary>Tries to read the tmux server entry from an environment.</summary>
    /// <param name="environment">The environment, or null for the process.</param>
    /// <param name="entry">The parsed entry when present and well formed.</param>
    /// <returns>True when the environment names a tmux server.</returns>
    internal static bool TryRead(
        IReadOnlyDictionary<string, string>? environment,
        [NotNullWhen(true)] out TmuxServerLocation? entry)
    {
        entry = null;
        string? value = Read(environment, ServerVariable);
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int sessionSeparator = value.LastIndexOf(',');
        int processSeparator = sessionSeparator > 0
            ? value.LastIndexOf(',', sessionSeparator - 1)
            : -1;
        if (processSeparator <= 0
            || sessionSeparator == value.Length - 1)
        {
            return false;
        }

        string socketPath = value[..processSeparator];
        string rawProcessId = value[(processSeparator + 1)..sessionSeparator];
        string rawSessionId = value[(sessionSeparator + 1)..];
        if (!int.TryParse(
                rawProcessId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId)
            || processId <= 0
            || rawProcessId != processId.ToString(CultureInfo.InvariantCulture)
            || !int.TryParse(
                rawSessionId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int sessionId)
            || sessionId < 0
            || rawSessionId != sessionId.ToString(CultureInfo.InvariantCulture))
        {
            return false;
        }

        entry = new TmuxServerLocation(socketPath, processId, new SessionId(sessionId));
        return true;
    }

    /// <summary>Reads the pane a process was spawned in.</summary>
    /// <param name="environment">The environment, or null for the process.</param>
    /// <param name="paneId">The parsed pane identifier.</param>
    /// <returns>True when the environment names a pane.</returns>
    internal static bool TryReadPane(
        IReadOnlyDictionary<string, string>? environment,
        out PaneId paneId) =>
        PaneId.TryParse(Read(environment, PaneVariable), out paneId);

    internal static bool HasPsmuxMarker(IReadOnlyDictionary<string, string>? environment) =>
        environment is null
            ? System.Environment.GetEnvironmentVariable(PsmuxSessionVariable) is not null
            : environment.Keys.Any(key => string.Equals(
                key,
                PsmuxSessionVariable,
                StringComparison.OrdinalIgnoreCase));

    internal static bool LooksLikePsmuxServer(IReadOnlyDictionary<string, string>? environment) =>
        Read(environment, ServerVariable)?.StartsWith("/tmp/psmux-", StringComparison.Ordinal)
            is true;

    private static string? Read(
        IReadOnlyDictionary<string, string>? environment,
        string name)
    {
        if (environment is null)
        {
            return System.Environment.GetEnvironmentVariable(name);
        }

        if (environment.TryGetValue(name, out string? value))
        {
            return value;
        }

        foreach ((string key, string? candidate) in environment)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
