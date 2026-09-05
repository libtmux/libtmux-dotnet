using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using LibTmux.Internal;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <summary>Turns what a caller named into the thing tmux holds.</summary>
/// <remarks>
/// <para>
/// Resolution is a single tmux query per call. Walking sessions, then their
/// windows, then their panes costs a process launch at every level to find
/// something tmux will answer in one, and the cost is paid on every tool call
/// rather than once.
/// </para>
/// <para>
/// A name that resolves to nothing is refused here, naming what was asked for,
/// rather than being passed on to fail somewhere the caller cannot connect to
/// what it typed.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class TmuxTargets
{
    /// <summary>Finds the pane a caller named, or the active one.</summary>
    /// <param name="server">The server to look in.</param>
    /// <param name="paneId">A pane identifier such as <c>%1</c>, or null for the active pane.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The pane.</returns>
    internal static async Task<Pane> PaneAsync(
        Server server,
        string? paneId,
        CancellationToken cancellationToken)
    {
        if (paneId is null)
        {
            return await ActivePaneAsync(server, cancellationToken).ConfigureAwait(false);
        }

        RequireNonEmpty(paneId, "paneId", "%1", "list_panes");

        string trimmed = paneId.Trim();
        if (!PaneId.TryParse(trimmed, out PaneId parsed))
        {
            throw new McpException(
                $"'{trimmed}' is not a pane id. A pane id looks like %1. "
                + "Call list_panes to see what exists.");
        }

        RaiseIfAbsent(server);

        // Listed rather than resolved by id. Resolving by id answers a pane
        // that knows its own identity and nothing else, so reading its options
        // or its server throws; listing materializes the relations the tools
        // actually use, and still costs one tmux call.
        IReadOnlyList<Pane> panes = await TmuxAvailability
            .OrEmptyAsync(server, () => server.GetPanesAsync(cancellationToken))
            .ConfigureAwait(false);
        foreach (Pane candidate in panes)
        {
            if (candidate.Id == parsed)
            {
                return candidate;
            }
        }

        throw new McpException(
            $"No pane {trimmed} exists. It may have been closed. "
            + "Call list_panes to see what does.");
    }

    /// <summary>Finds the window a caller named, or the active one.</summary>
    /// <param name="server">The server to look in.</param>
    /// <param name="windowId">A window identifier such as <c>@1</c>, or null for the active window.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The window.</returns>
    internal static async Task<Window> WindowAsync(
        Server server,
        string? windowId,
        CancellationToken cancellationToken)
    {
        if (windowId is null)
        {
            Pane active = await ActivePaneAsync(server, cancellationToken).ConfigureAwait(false);
            return active.Window;
        }

        RequireNonEmpty(windowId, "windowId", "@1", "list_windows");

        string trimmed = windowId.Trim();
        if (!WindowId.TryParse(trimmed, out WindowId parsed))
        {
            throw new McpException(
                $"'{trimmed}' is not a window id. A window id looks like @1. "
                + "Call list_windows to see what exists.");
        }

        RaiseIfAbsent(server);
        IReadOnlyList<Window> windows = await TmuxAvailability
            .OrEmptyAsync(server, () => server.GetWindowsAsync(cancellationToken))
            .ConfigureAwait(false);
        foreach (Window candidate in windows)
        {
            if (candidate.Id == parsed)
            {
                return candidate;
            }
        }

        throw new McpException(
            $"No window {trimmed} exists. It may have been closed. "
            + "Call list_windows to see what does.");
    }

    /// <summary>Finds the session a caller named by id or by name.</summary>
    /// <param name="server">The server to look in.</param>
    /// <param name="session">A session id such as <c>$1</c>, a session name, or null for the first one.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The session.</returns>
    /// <remarks>
    /// A name is accepted as well as an id because a session is the one level
    /// of the hierarchy people name themselves and then refer to by that name.
    /// </remarks>
    internal static async Task<Session> SessionAsync(
        Server server,
        string? session,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Session> sessions = await TmuxAvailability
            .OrEmptyAsync(server, () => server.GetSessionsAsync(cancellationToken))
            .ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            throw new McpException(
                "No tmux sessions are running. Call create_session to start one.");
        }

        if (session is null)
        {
            return sessions[0];
        }

        RequireNonEmpty(session, "session", "$1 or its name", "list_sessions");

        string trimmed = session.Trim();
        foreach (Session candidate in sessions)
        {
            if (string.Equals(candidate.Id.ToString(), trimmed, StringComparison.Ordinal)
                || string.Equals(candidate.Name, trimmed, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        string known = string.Join(", ", sessions.Select(each => $"{each.Id} ({each.Name})"));
        throw new McpException($"No session '{trimmed}' exists. These do: {known}.");
    }

    /// <summary>Finds the pane this process is running inside.</summary>
    /// <param name="server">The server to look in.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The pane, or null when this process is not running in one.</returns>
    /// <remarks>
    /// tmux sets <c>TMUX_PANE</c> in every pane it starts, so a server launched
    /// by a client inside tmux can name its own pane without being told. The
    /// lookup goes through the server this process is driving rather than the
    /// ambient one: those differ whenever a socket was named, and a pane from
    /// the wrong server would be a confident wrong answer.
    /// </remarks>
    internal static async Task<Pane?> CallerPaneAsync(
        Server server,
        CancellationToken cancellationToken)
    {
        if (await VerifiedCallerPaneIdAsync(server, cancellationToken).ConfigureAwait(false)
                is not string id
            || !PaneId.TryParse(id, out PaneId parsed)
            || !server.IsMaterialized)
        {
            return null;
        }

        try
        {
            foreach (Pane candidate in await server.GetPanesAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (candidate.Id == parsed)
                {
                    return candidate;
                }
            }

            return null;
        }
        catch (LibTmuxException)
        {
            // The variable outlives the pane it names: a shell that exported it
            // and then had its pane closed still carries it, and a socket the
            // caller named may not hold that pane at all. Failing to answer
            // "which pane am I in" is never worth failing the call over.
            return null;
        }
    }

    /// <summary>Finds the active pane of the server's first session.</summary>
    /// <param name="server">The server to look in.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The pane.</returns>
    /// <remarks>
    /// The caller's own pane wins when this process runs in one: a tool called
    /// with no target most often means "here".
    /// </remarks>
    internal static async Task<Pane> ActivePaneAsync(
        Server server,
        CancellationToken cancellationToken)
    {
        if (await CallerPaneAsync(server, cancellationToken).ConfigureAwait(false) is Pane caller)
        {
            return caller;
        }

        IReadOnlyList<Pane> panes = await TmuxAvailability
            .OrEmptyAsync(server, () => server.GetPanesAsync(cancellationToken))
            .ConfigureAwait(false);
        if (panes.Count == 0)
        {
            throw new McpException(
                "No tmux panes exist. Call create_session to start one.");
        }

        foreach (Pane pane in panes)
        {
            if (FormatFields.Flag(pane.RawFormatFields, "pane_active"))
            {
                return pane;
            }
        }

        return panes[0];
    }

    /// <summary>Answers the caller's pane, but only on the server that holds it.</summary>
    /// <param name="server">The server being driven.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The pane id, or null when the caller's pane is not on this server.</returns>
    /// <remarks>
    /// <c>TMUX_PANE</c> alone cannot answer this. tmux numbers panes per
    /// server, so <c>%1</c> in the terminal this conversation runs through and
    /// <c>%1</c> on the socket being driven are different panes whenever those
    /// are different servers — which is the ordinary case, because the server
    /// pins a dedicated socket by default. Believing the bare id marks an
    /// unrelated pane as the caller's own, protecting a scratch pane while
    /// leaving the real terminal unguarded.
    /// <c>TMUX</c> carries the socket path tmux exported into the pane, so
    /// comparing that against the socket actually being driven is what makes
    /// the id mean anything.
    /// </remarks>
    internal static async Task<string?> VerifiedCallerPaneIdAsync(
        Server server,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (CallerPaneId() is not string id || CallerSocketPath() is not string caller)
        {
            return null;
        }

        string? pinned = await SocketPathAsync(server, cancellationToken).ConfigureAwait(false);
        return pinned is not null && string.Equals(pinned, caller, StringComparison.Ordinal)
            ? id
            : null;
    }

    /// <summary>Answers the caller's pane id when it sits on a known socket.</summary>
    /// <param name="pinnedSocketPath">The socket this server drives, or null when unresolved.</param>
    /// <returns>The pane id, or null when it belongs to a different server.</returns>
    /// <remarks>
    /// The startup form of <see cref="VerifiedCallerPaneIdAsync" />, for the
    /// point where the socket path is already known and no server handle
    /// exists yet. An unresolved socket leaves the pane foreign, because an
    /// unverifiable claim about which terminal a model is talking through is
    /// worse than no claim.
    /// </remarks>
    internal static string? CallerPaneIdOn(string? pinnedSocketPath)
    {
        if (string.IsNullOrWhiteSpace(pinnedSocketPath)
            || CallerPaneId() is not string id
            || CallerSocketPath() is not string caller)
        {
            return null;
        }

        return string.Equals(Path.GetFullPath(pinnedSocketPath), caller, StringComparison.Ordinal)
            ? id
            : null;
    }

    /// <summary>Answers the socket path tmux exported into the caller's pane.</summary>
    /// <returns>The path, or null when this process is not running in a pane.</returns>
    /// <remarks>
    /// tmux writes <c>TMUX</c> as "socket-path,server-pid,session-id". Only the
    /// path is read: the pid and session id are frozen when the pane was
    /// spawned and go stale as soon as its window moves.
    /// </remarks>
    internal static string? CallerSocketPath() =>
        TmuxEnvironmentVariables.TryRead(null, out TmuxServerLocation? entry)
            ? Path.GetFullPath(entry.SocketPath)
            : null;

    private static readonly ConditionalWeakTable<Server, StrongBox<string?>> SocketPaths = new();

    /// <summary>Answers which socket a server is listening on, asking it once.</summary>
    /// <remarks>
    /// A connection made by name knows the name tmux was given, not the path
    /// tmux built from it, so the server itself is the only source. The path
    /// cannot change while a server runs, so it is remembered per handle. A
    /// server that will not answer is left unidentified, which keeps the
    /// caller's pane foreign rather than assuming it is ours.
    /// </remarks>
    private static async Task<string?> SocketPathAsync(
        Server server,
        CancellationToken cancellationToken)
    {
        if (SocketPaths.TryGetValue(server, out StrongBox<string?>? remembered))
        {
            return remembered.Value;
        }

        string? path = null;
        if (server.ConnectionOptions.SocketPath is string configured)
        {
            path = Path.GetFullPath(configured);
        }
        else if (server.IsMaterialized)
        {
            try
            {
                TmuxCommandResult result = await server.ExecuteCommandAsync(
                        ["display-message", "-p", "#{socket_path}"],
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.ExitCode == 0 && result.StandardOutputLines.Count == 1
                    && !string.IsNullOrWhiteSpace(result.StandardOutputLines[0]))
                {
                    path = Path.GetFullPath(result.StandardOutputLines[0]);
                }
            }
            catch (LibTmuxException)
            {
                // Unidentified is the safe answer; see the remarks.
            }
        }

        SocketPaths.AddOrUpdate(server, new StrongBox<string?>(path));
        return path;
    }

    /// <summary>Answers the identifier of the pane this process runs inside.</summary>
    /// <returns>The pane id, or null when this process is not running in one.</returns>
    internal static string? CallerPaneId()
    {
        string? value = System.Environment.GetEnvironmentVariable("TMUX_PANE");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Refuses plainly when there is no server to look in.</summary>
    /// <param name="server">The server a target was to be resolved against.</param>
    /// <remarks>
    /// An unmaterialized handle means nothing answered on the socket. Saying
    /// so beats letting the next call fail with a message about a missing
    /// version, which names neither the cause nor the cure.
    /// </remarks>
    internal static void RaiseIfAbsent(Server server)
    {
        if (!server.IsMaterialized)
        {
            throw new McpException(
                "No tmux server is running on that socket, so there is nothing to "
                + "target. Call create_session to start the pinned server, or correct "
                + "the startup socket setting.");
        }
    }

    /// <summary>Reads a tmux format field for one pane.</summary>
    /// <param name="pane">The pane to ask about.</param>
    /// <param name="format">The format string, such as <c>#{pane_dead}</c>.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The expanded text, or null when tmux answered nothing.</returns>
    internal static async Task<string?> DisplayAsync(
        Pane pane,
        string format,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? lines = await pane.DisplayMessageAsync(
                new DisplayMessageRequest(message: format, returnText: true),
                cancellationToken)
            .ConfigureAwait(false);
        return lines is { Count: > 0 } ? lines[0] : null;
    }

    /// <summary>Names where a spawn landed when tmux ignored the directory asked for.</summary>
    /// <param name="pane">The pane that was spawned, or null when none is known.</param>
    /// <param name="requested">The start directory the caller asked for.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>A sentence to append, or an empty string when there is nothing to say.</returns>
    /// <remarks>
    /// tmux does not refuse a start directory it cannot enter. It tries the
    /// requested path, then HOME, then <c>/</c>, and reports success either
    /// way, so an unqualified "created" leaves every command the caller runs
    /// afterwards executing somewhere they never chose.
    /// </remarks>
    internal static async Task<string> StartDirectoryNoteAsync(
        Pane? pane,
        string? requested,
        CancellationToken cancellationToken)
    {
        if (pane is null || string.IsNullOrWhiteSpace(requested))
        {
            return string.Empty;
        }

        // Normalised, so a request that only spells the same directory
        // differently — a trailing slash, a . or a .. segment — is recognised
        // as honoured rather than reported as a fallback.
        string asked = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
        string? actual = await DisplayAsync(pane, "#{pane_current_path}", cancellationToken)
            .ConfigureAwait(false);
        // Stated as where it landed rather than as a rejection: the two paths
        // come from different sides of a symlink often enough that claiming
        // tmux refused the request would sometimes be the wrong story.
        return actual is null
            || string.Equals(
                Path.TrimEndingDirectorySeparator(actual),
                asked,
                StringComparison.Ordinal)
            ? string.Empty
            : $" It started in {actual}; tmux does not refuse a start directory "
                + "it cannot use.";
    }

    // An empty string is a caller's bug, not an omission, and every resolver
    // used to read it as "whatever is current". That turned a mangled id into
    // a call against the active object, including for the tools that delete.
    private static void RequireNonEmpty(string value, string parameter, string shape, string listing)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new McpException(
                $"An empty {parameter} is not a target. Omit {parameter} for the current "
                + $"one, or name it like {shape}. Call {listing} to see what exists.");
        }
    }

    /// <summary>Resolves the option table one scope names.</summary>
    /// <param name="server">The server to resolve within.</param>
    /// <param name="scope">Which level the caller named.</param>
    /// <param name="paneId">The pane whose scope to take, or null for the active one.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The options at that scope.</returns>
    internal static async Task<TmuxOptions> OptionsAsync(
        Server server,
        OptionScope scope,
        string? paneId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        return scope switch
        {
            OptionScope.Server => server.Options,
            OptionScope.Session => (await PaneAsync(server, paneId, cancellationToken)
                .ConfigureAwait(false)).Session.Options,
            OptionScope.Window => (await PaneAsync(server, paneId, cancellationToken)
                .ConfigureAwait(false)).Window.Options,
            _ => (await PaneAsync(server, paneId, cancellationToken)
                .ConfigureAwait(false)).Options,
        };
    }

    /// <summary>Reads a tmux format field for one pane as a number.</summary>
    /// <param name="pane">The pane to ask about.</param>
    /// <param name="format">The format string, such as <c>#{history_size}</c>.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The number, or null when tmux answered something that is not one.</returns>
    internal static async Task<int?> DisplayNumberAsync(
        Pane pane,
        string format,
        CancellationToken cancellationToken)
    {
        string? text = await DisplayAsync(pane, format, cancellationToken).ConfigureAwait(false);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }
}
