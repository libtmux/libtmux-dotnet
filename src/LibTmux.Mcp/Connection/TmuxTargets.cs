using System.Globalization;
using System.Runtime.Versioning;
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
        if (string.IsNullOrWhiteSpace(paneId))
        {
            return await ActivePaneAsync(server, cancellationToken).ConfigureAwait(false);
        }

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
        if (string.IsNullOrWhiteSpace(windowId))
        {
            Pane active = await ActivePaneAsync(server, cancellationToken).ConfigureAwait(false);
            return active.Window;
        }

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

        if (string.IsNullOrWhiteSpace(session))
        {
            return sessions[0];
        }

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
        if (CallerPaneId() is not string id
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
