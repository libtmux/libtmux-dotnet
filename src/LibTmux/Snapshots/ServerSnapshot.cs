using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;
using LibTmux.Query;

namespace LibTmux;

// Builds the copy-backed hierarchy graph carried by a materialized Server.
// Sessions and windows share handles so walking down and back up preserves state.
internal sealed class ServerSnapshot
{
    internal ServerSnapshot(
        SnapshotDepth depth,
        CapturedRelation<Session> sessions,
        CapturedRelation<Window> windows,
        CapturedRelation<Pane> panes)
    {
        Depth = depth;
        Sessions = sessions;
        Windows = windows;
        Panes = panes;
    }

    internal SnapshotDepth Depth { get; }

    internal CapturedRelation<Session> Sessions { get; }

    internal CapturedRelation<Window> Windows { get; }

    internal CapturedRelation<Pane> Panes { get; }

    internal sealed record Rows(
        SnapshotDepth Depth,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> Sessions,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> Windows,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> Panes,
        IReadOnlyList<bool>? QueryMatches = null);

    [UnsupportedOSPlatform("windows")]
    internal static Task<Rows> ReadAsync(
        Server server,
        SnapshotDepth depth = SnapshotDepth.Panes,
        CancellationToken cancellationToken = default) =>
        ReadAsync(server, depth, null, null, null, cancellationToken);

    [UnsupportedOSPlatform("windows")]
    internal static async Task<Rows> ReadAsync(
        Server server,
        SnapshotDepth depth,
        TmuxVersion? projectionVersion,
        QueryTarget? queryTarget,
        string? predicateFormat,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        cancellationToken.ThrowIfCancellationRequested();
        ServerGeneration generation = server.Generation
            ?? throw new InvalidOperationException(
                "The server has no live generation; connect before capturing.");
        var context = new MaterializationContext(server, projectionVersion ?? ParseVersion(server));
        var query = new MaterializationQuery(context);
        IReadOnlyList<bool>? matches = null;
        if (depth == SnapshotDepth.Server)
        {
            TmuxCommandResult result = await server.Connection!
                .CreateEntityDispatcher(generation)
                .ExecuteAsync(["display-message", "-p", TmuxConnection.GenerationFormat], cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new TmuxCommandException("Snapshot generation read failed.", result);
            }
            if (result.StandardOutputLines.Count != 1)
            {
                throw new TmuxProtocolException("tmux did not report exactly one snapshot generation.", TmuxDispatchState.Dispatched);
            }
            context.EnsureOwns(TmuxConnection.ParseGeneration(result.StandardOutputLines[0]));
            cancellationToken.ThrowIfCancellationRequested();
            return new Rows(depth, [], [], []);
        }

        IReadOnlyList<IReadOnlyDictionary<string, string?>> sessionRows =
            await Fetch("list-sessions", null)
                .ConfigureAwait(false);
        if (depth == SnapshotDepth.Sessions)
        {
            SnapshotTopologyValidator.Validate(depth, generation, sessionRows, [], [], cancellationToken);
            return new Rows(depth, sessionRows, [], [], matches);
        }

        IReadOnlyList<IReadOnlyDictionary<string, string?>> windowRows =
            await Fetch("list-windows", ["-a"])
                .ConfigureAwait(false);
        IReadOnlyList<IReadOnlyDictionary<string, string?>> paneRows =
            depth < SnapshotDepth.Panes
                ? []
                : await Fetch("list-panes", ["-a"])
                    .ConfigureAwait(false);
        SnapshotTopologyValidator.Validate(depth, generation, sessionRows, windowRows, paneRows, cancellationToken);
        return new Rows(depth, sessionRows, windowRows, paneRows, matches);

        async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> Fetch(string command, string[]? arguments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicateFormat is not null && queryTarget is { } target && command == QuerySourcePlanner.ListCommand(target))
            {
                MaterializedQueryRows marked = await query.FetchMarkedAsync(command, arguments, predicateFormat, cancellationToken)
                    .ConfigureAwait(false);
                matches = marked.Matches;
                return marked.Fields;
            }
            return await query.FetchAsync(command, arguments, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ServerSnapshot Empty(SnapshotDepth depth) =>
        new(
            depth,
            CapturedRelation.Uncaptured<Session>("sessions", depth),
            CapturedRelation.Uncaptured<Window>("windows", depth),
            CapturedRelation.Uncaptured<Pane>("panes", depth));

    [UnsupportedOSPlatform("windows")]
    internal static ServerSnapshot Build(Server server, Rows rows, CancellationToken cancellationToken)
    {
        SnapshotDepth depth = rows.Depth;
        if (depth == SnapshotDepth.Server)
        {
            return Empty(depth);
        }

        Session[] sessions = [.. rows.Sessions.Select(row =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return RelationReader.ToSession(server, row);
        })];
        var sessionsById = sessions.ToDictionary(session => session.Id);
        Pane[] panes = [.. rows.Panes.Select(row =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return RelationReader.ToPane(server, row);
        })];
        var panesByWindow = panes.ToLookup(pane => Placement(pane.RawFormatFields));
        var panesBySession = panes.ToLookup(pane => Placement(pane.RawFormatFields).SessionId);
        var paneByPlacement = panes.ToDictionary(pane => (Placement(pane.RawFormatFields), pane.Id));
        SessionWindowEdge[] edges = BuildEdges(rows.Windows, cancellationToken);
        var linkedSessions = edges.GroupBy(edge => edge.WindowId).ToDictionary(
            group => group.Key,
            group => Relation(
                group.Select(edge => edge.SessionId).Distinct().Select(id => sessionsById[id]).ToArray(),
                "linked sessions",
                depth));
        Window[] windows = [.. rows.Windows.Select((row, index) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Window window = RelationReader.ToWindow(server, row);
            SessionWindowEdge edge = edges[index];
            Pane? activePane = depth >= SnapshotDepth.Panes
                ? ActivePane(row, edge.Key)
                : null;
            window.WithCaptured(
                Relation(panesByWindow[edge.Key].ToArray(), "panes", depth, depth >= SnapshotDepth.Panes),
                linkedSessions[window.Id],
                edge,
                sessionsById[edge.SessionId],
                activePane);
            foreach (Pane pane in panesByWindow[edge.Key])
            {
                cancellationToken.ThrowIfCancellationRequested();
                pane.WithCaptured(window);
            }
            return window;
        })];
        var windowsBySession = windows.ToLookup(window => window.Edge.SessionId);
        var windowByPlacement = windows.ToDictionary(window => window.EntityKey);
        foreach (Session session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Window? activeWindow = null;
            Pane? activePane = null;
            if (depth >= SnapshotDepth.Windows)
            {
                WindowEntityKey key = Placement(session.RawFormatFields);
                if (!windowByPlacement.TryGetValue(key, out activeWindow))
                {
                    throw Inconsistent($"The active window placement '{key}' was not acquired.");
                }
                if (depth >= SnapshotDepth.Panes)
                {
                    activePane = ActivePane(session.RawFormatFields, key);
                }
            }
            session.WithCaptured(
                Relation(windowsBySession[session.Id].ToArray(), "windows", depth, depth >= SnapshotDepth.Windows),
                Relation(panesBySession[session.Id].ToArray(), "panes", depth, depth >= SnapshotDepth.Panes),
                activeWindow,
                activePane);
        }

        return new ServerSnapshot(
            depth,
            Relation(sessions, "sessions", depth),
            Relation(windows, "windows", depth, depth >= SnapshotDepth.Windows),
            Relation(panes, "panes", depth, depth >= SnapshotDepth.Panes));

        Pane ActivePane(IReadOnlyDictionary<string, string?> row, WindowEntityKey key) =>
            PaneId.TryParse(Field(row, "pane_id"), out PaneId id)
                && paneByPlacement.TryGetValue((key, id), out Pane? pane)
                    ? pane
                    : throw Inconsistent($"The active pane in window placement '{key}' was not acquired.");

        InconsistentSnapshotException Inconsistent(string message) =>
            new(message, depth, server.Generation!.Value);
    }

    private static WindowEntityKey Placement(IReadOnlyDictionary<string, string?> row) =>
        new(
            SessionId.Parse(Read(row, "session_id")),
            WindowId.Parse(Read(row, "window_id")),
            int.Parse(Read(row, "window_index"), NumberStyles.None, CultureInfo.InvariantCulture));

    private static CapturedRelation<T> Relation<T>(
        IReadOnlyList<T> items,
        string relation,
        SnapshotDepth depth,
        bool captured = true) =>
        captured
            ? CapturedRelation.Capture(items, relation, depth)
            : CapturedRelation.Uncaptured<T>(relation, depth);

    private static SessionWindowEdge[] BuildEdges(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> windowRows,
        CancellationToken cancellationToken)
    {
        var ordinals = new Dictionary<SessionId, int>();
        var edges = new List<SessionWindowEdge>(windowRows.Count);
        foreach (IReadOnlyDictionary<string, string?> row in windowRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SessionId.TryParse(Read(row, "session_id"), out SessionId sessionId)
                || !WindowId.TryParse(Read(row, "window_id"), out WindowId windowId)
                || !int.TryParse(
                    Read(row, "window_index"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int windowIndex))
            {
                throw new TmuxProtocolException("tmux reported a malformed window edge.", TmuxDispatchState.Dispatched);
            }

            // "list-windows -a" walks sessions in order, so the running count
            // per session is the window's position within that session.
            ordinals.TryGetValue(sessionId, out int ordinal);
            ordinals[sessionId] = ordinal + 1;
            edges.Add(
                new SessionWindowEdge
                {
                    SessionId = sessionId,
                    WindowId = windowId,
                    WindowIndex = windowIndex,
                    Ordinal = ordinal,
                });
        }

        return [.. edges];
    }

    private static string? Field(IReadOnlyDictionary<string, string?> row, string wireName) =>
        row.TryGetValue(wireName, out string? value) ? value : null;

    private static string Read(IReadOnlyDictionary<string, string?> row, string wireName) =>
        Field(row, wireName)
        ?? throw new TmuxProtocolException($"tmux row is missing '{wireName}'.", TmuxDispatchState.Dispatched);

    private static TmuxVersion ParseVersion(Server server)
    {
        string raw = server.RawVersion
            ?? throw new InvalidOperationException("The server reported no tmux version.");
        return TmuxVersion.Parse(
            raw.StartsWith("tmux ", StringComparison.Ordinal) ? raw[5..] : raw);
    }
}
