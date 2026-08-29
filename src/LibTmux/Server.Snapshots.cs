using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Captured relations never query tmux and distinguish uncaptured data from an
// observed empty relation.
public sealed partial class Server
{
    private readonly ServerSnapshot? _snapshot;

    private Server(
        TmuxConnection connection,
        ServerGeneration? generation,
        string? rawVersion,
        ServerSnapshot snapshot)
        : this(connection, generation, rawVersion) =>
        _snapshot = snapshot;

    /// <summary>Gets the sessions this handle captured.</summary>
    public CapturedRelation<Session> Sessions =>
        _snapshot?.Sessions ?? CapturedRelation.Uncaptured<Session>("sessions", Depth);

    /// <summary>Gets the windows this handle captured, across every session.</summary>
    /// <remarks>
    /// A window linked into several sessions was read once per session, so it
    /// appears here once per session it is linked into. Which session each one
    /// belongs to is read from the window rather than from this list.
    /// </remarks>
    public CapturedRelation<Window> Windows =>
        _snapshot?.Windows ?? CapturedRelation.Uncaptured<Window>("windows", Depth);

    /// <summary>Gets the panes this handle captured, across every window.</summary>
    /// <remarks>
    /// tmux lists panes per window rather than per server, so a capture that
    /// stopped short of panes leaves this uncaptured even when the windows are
    /// there.
    /// </remarks>
    public CapturedRelation<Pane> Panes =>
        _snapshot?.Panes ?? CapturedRelation.Uncaptured<Pane>("panes", Depth);

    /// <summary>Gets the clients this handle captured.</summary>
    /// <remarks>
    /// A capture reads the hierarchy, which clients are not part of: a client
    /// is attached to a session rather than contained by one. Reading them is
    /// <see cref="GetClientsAsync" />.
    /// </remarks>
    public CapturedRelation<Client> Clients =>
        CapturedRelation.Uncaptured<Client>("clients", Depth);

    /// <summary>Reads the server and answers a handle carrying what it found.</summary>
    /// <param name="depth">How far down the hierarchy to read.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>A handle whose relations are the ones this reading found.</returns>
    /// <exception cref="InvalidOperationException">The handle has no connection.</exception>
    /// <remarks>
    /// A handle that has not yet found a live server discovers one first,
    /// because a scope hands back the unmaterialized endpoint it started.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Server> CaptureSnapshotAsync(
        SnapshotDepth depth = SnapshotDepth.Panes,
        CancellationToken cancellationToken = default)
    {
        TmuxConnection connection = _connection
            ?? throw new InvalidOperationException("The server handle has no connection.");
        Server live = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        ServerSnapshot snapshot = await ServerSnapshot
            .CaptureAsync(live, depth, cancellationToken)
            .ConfigureAwait(false);

        // Returns a new handle rather than mutating this one, so a caller
        // already holding it keeps seeing what it originally read.
        return new Server(connection, live.Generation, live.RawVersion, snapshot);
    }

    private SnapshotDepth Depth => _snapshot?.Depth ?? SnapshotDepth.Server;
}
