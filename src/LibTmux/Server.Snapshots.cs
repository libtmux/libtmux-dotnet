using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Captured relations never query tmux and distinguish uncaptured data from an
// observed empty relation.
public sealed partial class Server
{
    private readonly ServerSnapshot? _snapshot;

    /// <summary>Gets acquisition metadata, or null when this handle was not captured.</summary>
    public SnapshotMetadata? SnapshotMetadata { get; }

    [UnsupportedOSPlatform("windows")]
    private Server(
        TmuxConnection connection,
        ServerGeneration? generation,
        string? rawVersion,
        TmuxVersion? daemonVersion,
        ServerSnapshot.Rows rows,
        TimeProvider timeProvider,
        long started,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
        : this(connection, generation, rawVersion, daemonVersion)
    {
        _snapshot = ServerSnapshot.Build(this, rows, cancellationToken);
        SnapshotMetadata = new SnapshotMetadata(
            rows.Depth,
            generation!.Value,
            startedAtUtc,
            timeProvider.GetUtcNow(),
            timeProvider.GetElapsedTime(started));
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Gets the sessions this handle captured.</summary>
    public CapturedRelation<Session> Sessions =>
        _snapshot?.Sessions ?? CapturedRelation.Uncaptured<Session>("sessions", Depth);

    /// <summary>Gets the windows this handle captured, across every session.</summary>
    /// <remarks>
    /// Each session/index placement appears separately, including repeated links
    /// in one session. Each handle preserves its captured parent and children.
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
    /// <exception cref="ArgumentOutOfRangeException">The depth is undefined.</exception>
    /// <exception cref="InconsistentSnapshotException">The acquired parent, placement, or child-count rows contradict each other.</exception>
    /// <exception cref="StaleServerGenerationException">The endpoint now names a different daemon generation.</exception>
    /// <exception cref="OperationCanceledException">Acquisition was cancelled before publication.</exception>
    /// <remarks>
    /// A handle that has not yet found a live server discovers one first,
    /// because a scope hands back the unmaterialized endpoint it started.
    /// <para>
    /// Every depth performs a fresh guarded read. Separate reads are not an
    /// atomic observation; detected topology contradictions fail the attempt
    /// without retrying or publishing partial relations.
    /// </para>
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task<Server> CaptureSnapshotAsync(
        SnapshotDepth depth = SnapshotDepth.Panes,
        CancellationToken cancellationToken = default) =>
        CaptureSnapshotAsync(depth, TimeProvider.System, cancellationToken);

    [UnsupportedOSPlatform("windows")]
    internal async Task<Server> CaptureSnapshotAsync(
        SnapshotDepth depth,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (depth is < SnapshotDepth.Server or > SnapshotDepth.Panes)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "The snapshot depth is undefined.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        TmuxConnection connection = _connection
            ?? throw new InvalidOperationException("The server handle has no connection.");
        long started = timeProvider.GetTimestamp();
        DateTimeOffset startedAtUtc = timeProvider.GetUtcNow();
        Server live = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        ServerSnapshot.Rows rows = await ServerSnapshot
            .ReadAsync(live, depth, cancellationToken)
            .ConfigureAwait(false);

        // Only the newly constructed root owns these children; earlier captures
        // and refreshed handles keep their original graph membership.
        return new Server(
            connection, live.Generation, live.RawVersion, live.DaemonVersion, rows,
            timeProvider, started, startedAtUtc, cancellationToken);
    }

    private SnapshotDepth Depth => _snapshot?.Depth ?? SnapshotDepth.Server;
}
