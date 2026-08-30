using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

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

    [UnsupportedOSPlatform("windows")]
    internal static async Task<ServerSnapshot> CaptureAsync(
        Server server,
        SnapshotDepth depth = SnapshotDepth.Panes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        _ = server.Generation
            ?? throw new InvalidOperationException(
                "The server has no live generation; connect before capturing.");
        var context = new MaterializationContext(server, ParseVersion(server));
        var query = new MaterializationQuery(context);
        if (depth == SnapshotDepth.Server)
        {
            return Empty(depth);
        }

        IReadOnlyList<IReadOnlyDictionary<string, string?>> sessionRows =
            await query.FetchAsync("list-sessions", null, cancellationToken)
                .ConfigureAwait(false);
        if (depth == SnapshotDepth.Sessions)
        {
            return new ServerSnapshot(
                depth,
                CapturedRelation.Capture(
                    [.. sessionRows.Select(row => RelationReader.ToSession(server, row))],
                    "sessions",
                    depth),
                CapturedRelation.Uncaptured<Window>("windows", depth),
                CapturedRelation.Uncaptured<Pane>("panes", depth));
        }

        IReadOnlyList<IReadOnlyDictionary<string, string?>> windowRows =
            await query.FetchAsync("list-windows", ["-a"], cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyList<IReadOnlyDictionary<string, string?>> paneRows =
            depth < SnapshotDepth.Panes
                ? []
                : await query.FetchAsync("list-panes", ["-a"], cancellationToken)
                    .ConfigureAwait(false);
        return Build(server, depth, sessionRows, windowRows, paneRows);
    }

    private static ServerSnapshot Empty(SnapshotDepth depth) =>
        new(
            depth,
            CapturedRelation.Uncaptured<Session>("sessions", depth),
            CapturedRelation.Uncaptured<Window>("windows", depth),
            CapturedRelation.Uncaptured<Pane>("panes", depth));

    [UnsupportedOSPlatform("windows")]
    private static ServerSnapshot Build(
        Server server,
        SnapshotDepth depth,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> sessionRows,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> windowRows,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> paneRows)
    {
        SessionWindowEdge[] edges = BuildEdges(windowRows);
        Pane[] panes = [.. paneRows.Select(row => RelationReader.ToPane(server, row))];

        // Sessions and windows reference each other, so sessions are built first
        // with a mutable window list that is filled in once windows exist.
        var windowsBySession = new Dictionary<SessionId, List<Window>>();
        Session[] sessions =
        [
            .. sessionRows.Select(row =>
            {
                Session session = RelationReader.ToSession(server, row);
                windowsBySession[session.Id] = [];
                return session.WithCaptured(
                    () => Relation(windowsBySession[session.Id], "windows", depth),
                    Relation(
                        [.. panes.Where(pane => Owns(paneRows, pane, "session_id", session.Id.ToString()))],
                        "panes",
                        depth,
                        depth >= SnapshotDepth.Panes));
            }),
        ];
        var sessionsById = sessions.ToDictionary(session => session.Id);

        Window[] windows =
        [
            .. windowRows.Select(row =>
            {
                Window window = RelationReader.ToWindow(server, row);
                SessionWindowEdge? edge = edges.FirstOrDefault(candidate =>
                    candidate.WindowId == window.Id
                    && candidate.SessionId.ToString() == Field(row, "session_id"));
                Session[] linked =
                [
                    .. edges
                        .Where(candidate => candidate.WindowId == window.Id)
                        .Select(candidate => sessionsById.GetValueOrDefault(candidate.SessionId))
                        .OfType<Session>(),
                ];
                return window.WithCaptured(
                    Relation(
                        [.. panes.Where(pane => Owns(paneRows, pane, "window_id", window.Id.ToString()))],
                        "panes",
                        depth,
                        depth >= SnapshotDepth.Panes),
                    Relation(linked, "linked sessions", depth),
                    edge);
            }),
        ];
        foreach (Window window in windows)
        {
            if (window.Edge is SessionWindowEdge edge
                && windowsBySession.TryGetValue(edge.SessionId, out List<Window>? owned))
            {
                owned.Add(window);
            }
        }

        return new ServerSnapshot(
            depth,
            Relation(sessions, "sessions", depth),
            Relation(windows, "windows", depth),
            Relation(panes, "panes", depth, depth >= SnapshotDepth.Panes));
    }

    private static CapturedRelation<T> Relation<T>(
        IReadOnlyList<T> items,
        string relation,
        SnapshotDepth depth,
        bool captured = true) =>
        captured
            ? CapturedRelation.Capture(items, relation, depth)
            : CapturedRelation.Uncaptured<T>(relation, depth);

    private static bool Owns(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        Pane pane,
        string wireName,
        string owner) =>
        rows.Any(row =>
            Field(row, "pane_id") == pane.Id.ToString() && Field(row, wireName) == owner);

    private static SessionWindowEdge[] BuildEdges(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> windowRows)
    {
        var ordinals = new Dictionary<SessionId, int>();
        var edges = new List<SessionWindowEdge>(windowRows.Count);
        foreach (IReadOnlyDictionary<string, string?> row in windowRows)
        {
            if (!SessionId.TryParse(Read(row, "session_id"), out SessionId sessionId)
                || !WindowId.TryParse(Read(row, "window_id"), out WindowId windowId)
                || !int.TryParse(
                    Read(row, "window_index"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int windowIndex))
            {
                throw new InvalidDataException("tmux reported a malformed window edge.");
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
        ?? throw new InvalidDataException($"tmux window row is missing '{wireName}'.");

    private static TmuxVersion ParseVersion(Server server)
    {
        string raw = server.RawVersion
            ?? throw new InvalidOperationException("The server reported no tmux version.");
        return TmuxVersion.Parse(
            raw.StartsWith("tmux ", StringComparison.Ordinal) ? raw[5..] : raw);
    }
}
