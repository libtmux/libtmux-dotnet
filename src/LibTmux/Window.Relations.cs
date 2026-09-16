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
    private Session? _capturedSession;
    private CapturedRelation<Pane>? _activePane;

    /// <summary>Gets the captured active pane, or an uncaptured relation.</summary>
    /// <remarks>
    /// Reading this is local. A window reached through an inactive pane
    /// carries that pane's row, so its active pane was not captured.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public CapturedRelation<Pane> ActivePane =>
        _activePane ??= ReadSnapshot("pane_active") == "1"
            ? CapturedRelation.Capture(
                [ReadActivePane()],
                "active pane",
                SnapshotDepth.Windows)
            : CapturedRelation.Uncaptured<Pane>("active pane", SnapshotDepth.Windows);

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
    /// <remarks>
    /// A window has one edge per indexed placement, including multiple indexes
    /// within one session. This is the edge for the session and index captured
    /// by this handle.
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
    /// <remarks>
    /// tmux links one window into several sessions at different indexes, so a
    /// window identifier alone does not name a place in the hierarchy.
    /// </remarks>
    public WindowEntityKey EntityKey =>
        SessionId.TryParse(ReadSnapshot("session_id"), out SessionId session)
            ? new WindowEntityKey(session, _id)
            : throw new IncompleteSnapshotException("entity key", SnapshotDepth.Windows);

    private int ReadIndex() =>
        TryReadIndex(out int index)
            ? index
            : throw new IncompleteSnapshotException("window index", SnapshotDepth.Windows);

    private bool TryReadIndex(out int index) =>
        int.TryParse(
            ReadSnapshot("window_index"),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out index);

    /// <summary>Names this window scoped to the session it was captured in.</summary>
    /// <remarks>
    /// A window linked into its captured session at another index too shares
    /// its id with that placement, so this prefers the session and the
    /// captured index, which names the placement uniquely: tmux's window-id
    /// target would resolve to whichever placement it ranks best, not the one
    /// this handle was read at. Falling back to the session and the window id
    /// when the index was not captured still narrows a bare identifier to the
    /// session this handle can prove.
    /// </remarks>
    private TmuxTarget? ScopedTarget() =>
        RelationReader.CapturedSession(_snapshot) is not SessionId session
            ? null
            : TryReadIndex(out int index)
                ? TmuxTarget.In(session, index)
                : TmuxTarget.In(session, _id);

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
                    ["-t", ScopedTarget()?.Value ?? _id.ToString()],
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
        SessionWindowEdge? edge,
        Session? session)
    {
        _panes = panes;
        _linkedSessions = linkedSessions;
        _edge = edge;
        _capturedSession = session;
        return this;
    }

    [UnsupportedOSPlatform("windows")]
    private Pane ReadActivePane()
    {
        CapturedRelation<Pane> panes = Panes;
        Pane? captured = panes.IsCaptured
            ? panes.FirstOrDefault(pane => pane.Id.ToString() == ReadSnapshot("pane_id"))
            : null;
        return captured ?? RelationReader.ToPane(RequireOwner("active pane"), RawFormatFields);
    }

    private Server RequireOwner(string relation) =>
        _owner ?? throw new IncompleteSnapshotException(relation, SnapshotDepth.Server);

    private string? ReadSnapshot(string wireName) =>
        _snapshot is not null && _snapshot.TryGetValue(wireName, out string? value)
            ? value
            : null;
}
