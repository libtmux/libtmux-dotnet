using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Provides window hierarchy relations.
public sealed partial class Window
{
    private readonly Server? _owner;
    private CapturedRelation<Pane>? _panes;
    private CapturedRelation<Session>? _linkedSessions;
    private SessionWindowEdge? _edge;

    /// <summary>Gets the active pane recorded when this window was read.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Pane ActivePane
    {
        get
        {
            if (!PaneId.TryParse(ReadSnapshot("pane_id"), out PaneId id))
            {
                throw new IncompleteSnapshotException("active pane", SnapshotDepth.Windows);
            }

            return new Pane(RequireOwner("panes"), RequireConnection(), _generation, id);
        }
    }

    /// <summary>Gets the panes the capture found in this window.</summary>
    /// <remarks>Reading this never reaches tmux.</remarks>
    public CapturedRelation<Pane> Panes =>
        _panes ?? CapturedRelation.Uncaptured<Pane>("panes", SnapshotDepth.Server);

    /// <summary>Gets the sessions the capture found this window linked into.</summary>
    /// <remarks>Reading this never reaches tmux.</remarks>
    public CapturedRelation<Session> LinkedSessions =>
        _linkedSessions
        ?? CapturedRelation.Uncaptured<Session>("linked sessions", SnapshotDepth.Server);

    /// <summary>Gets where this window sits in the session it was read from.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    /// <remarks>
    /// A window linked into several sessions has one edge per session. This is
    /// the edge for the session this handle was read through, which is why the
    /// handle can answer it at all.
    /// </remarks>
    public SessionWindowEdge Edge
    {
        get
        {
            if (_edge is not null)
            {
                return _edge;
            }

            WindowEntityKey key = EntityKey;
            return new SessionWindowEdge
            {
                SessionId = key.SessionId,
                WindowId = key.WindowId,
                WindowIndex = ReadIndex(),
            };
        }
    }

    /// <summary>Gets the session and window this handle names together.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    /// <remarks>
    /// tmux links one window into several sessions at different indexes, so a
    /// window identifier alone does not name a place in the hierarchy.
    /// </remarks>
    public WindowEntityKey EntityKey =>
        SessionId.TryParse(ReadSnapshot("session_id"), out SessionId session)
            ? new WindowEntityKey(session, _id)
            : throw new IncompleteSnapshotException("entity key", SnapshotDepth.Windows);

    private int ReadIndex() =>
        int.TryParse(
            ReadSnapshot("window_index"),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int index)
            ? index
            : throw new IncompleteSnapshotException("window index", SnapshotDepth.Windows);

    /// <summary>Reads this window's panes from tmux.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The panes tmux reports for this window.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Pane>> GetPanesAsync(
        CancellationToken cancellationToken = default)
    {
        Server owner = RequireOwner("panes");
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
            await RelationReader.ListAsync(
                    owner,
                    "list-panes",
                    ["-t", _id.ToString()],
                    cancellationToken)
                .ConfigureAwait(false);
        return [.. rows.Select(row => RelationReader.ToPane(owner, row))];
    }

    /// <summary>Reads every session this window is linked into.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The sessions that link this window.</returns>
    /// <remarks>
    /// tmux can link one window into several sessions, so this reports every
    /// session holding the window rather than a single parent.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Session>> GetLinkedSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        Server owner = RequireOwner("linked sessions");
        IReadOnlyList<IReadOnlyDictionary<string, string?>> windows =
            await RelationReader.ListAsync(owner, "list-windows", ["-a"], cancellationToken)
                .ConfigureAwait(false);
        string id = _id.ToString();
        HashSet<string> linked =
        [
            .. windows
                .Where(row => row.TryGetValue("window_id", out string? value) && value == id)
                .Select(row => row["session_id"])
                .OfType<string>(),
        ];
        IReadOnlyList<IReadOnlyDictionary<string, string?>> sessions =
            await RelationReader.ListAsync(owner, "list-sessions", [], cancellationToken)
                .ConfigureAwait(false);
        return
        [
            .. sessions
                .Where(row => row.TryGetValue("session_id", out string? value)
                    && value is not null
                    && linked.Contains(value))
                .Select(row => RelationReader.ToSession(owner, row)),
        ];
    }

    internal Window WithCaptured(
        CapturedRelation<Pane> panes,
        CapturedRelation<Session> linkedSessions,
        SessionWindowEdge? edge)
    {
        _panes = panes;
        _linkedSessions = linkedSessions;
        _edge = edge;
        return this;
    }

    private Server RequireOwner(string relation) =>
        _owner ?? throw new IncompleteSnapshotException(relation, SnapshotDepth.Server);

    private TmuxConnection RequireConnection() =>
        RequireOwner("connection").Connection
        ?? throw new IncompleteSnapshotException("connection", SnapshotDepth.Server);

    private string? ReadSnapshot(string wireName) =>
        _snapshot is not null && _snapshot.TryGetValue(wireName, out string? value)
            ? value
            : null;
}
