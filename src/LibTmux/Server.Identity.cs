using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Provides server connection identity and typed lookup.
public sealed partial class Server : IEquatable<Server>
{
    private readonly TmuxConnection? _connection;
    private readonly ServerGeneration? _generation;
    private readonly string? _rawVersion;

    internal Server(
        TmuxConnection connection,
        ServerGeneration? generation,
        string? rawVersion)
        : this(connection.ServerDispatcher)
    {
        _connection = connection;
        _generation = generation;
        _rawVersion = rawVersion;
    }

    /// <summary>Gets the connection options.</summary>
    public ServerConnectionOptions ConnectionOptions =>
        _connection?.Options ?? ServerConnectionOptions.Default;

    /// <summary>Gets the materialized server generation.</summary>
    public ServerGeneration? Generation => _generation;

    /// <summary>Gets whether this handle has discovered a live server.</summary>
    public bool IsMaterialized => _generation.HasValue;

    internal string? RawVersion => _rawVersion;

    internal TmuxConnection? Connection => _connection;

    /// <summary>Opens an unmaterialized server connection handle.</summary>
    public static Server Open(ServerConnectionOptions? options = null)
    {
        ServerConnectionOptions effectiveOptions = options ?? ServerConnectionOptions.Default;
        return new Server(new TmuxConnection(effectiveOptions), generation: null, rawVersion: null);
    }

    /// <summary>Connects to a configured tmux endpoint.</summary>
    [UnsupportedOSPlatform("windows")]
    public static Task<Server> ConnectAsync(
        ServerConnectionOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Open(options).ConnectAsync(cancellationToken);

    /// <summary>Materializes this connection and returns its immutable replacement.</summary>
    [UnsupportedOSPlatform("windows")]
    public async Task<Server> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("This server has no connection identity.");
        }

        if (IsMaterialized)
        {
            return this;
        }

        return await RediscoverCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<Server> RediscoverCurrentGenerationAsync(
        CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("This server has no connection identity.");
        }

        (ServerGeneration generation, string rawVersion) = await _connection
            .DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        if (_generation is ServerGeneration existing && existing == generation)
        {
            return this;
        }

        var materialized = new Server(_connection, generation, rawVersion);
        if (ConnectionOptions.InitializeAsync is not null)
        {
            await ConnectionOptions.InitializeAsync(materialized, cancellationToken)
                .ConfigureAwait(false);
        }

        return materialized;
    }

    /// <summary>Reads one session by identifier, throwing when it is absent.</summary>
    /// <param name="id">The session identifier.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A materialized session carrying captured scalar state.</returns>
    /// <exception cref="TmuxObjectNotFoundException">The session does not exist.</exception>
    /// <exception cref="LibTmuxException">The lookup failed, including an absent daemon.</exception>
    /// <remarks>A handle that has not found a live server yet discovers one first.</remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Session> GetSessionAsync(
        SessionId id,
        CancellationToken cancellationToken = default) =>
        await FindSessionAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new TmuxObjectNotFoundException($"Session {id} was not found.", id.ToString());

    /// <summary>Reads one session by identifier, returning null when it is absent.</summary>
    /// <param name="id">The session identifier.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A materialized session, or null after a successful read finds no match.</returns>
    /// <exception cref="LibTmuxException">The lookup failed, including an absent daemon.</exception>
    /// <remarks>A handle that has not found a live server yet discovers one first.</remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Session?> FindSessionAsync(
        SessionId id,
        CancellationToken cancellationToken = default)
    {
        Server owner = await ListingOwnerAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string?>? row = await RelationReader.FindAsync(
                owner,
                "list-sessions",
                "session_id",
                id.ToString(),
                inSession: null,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : RelationReader.ToSession(owner, row);
    }

    /// <summary>Reads one window by identifier, throwing when it is absent.</summary>
    /// <param name="id">The window identifier.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A materialized window carrying captured scalar state.</returns>
    /// <exception cref="TmuxObjectNotFoundException">The window does not exist.</exception>
    /// <exception cref="LibTmuxException">The lookup failed, including an absent daemon.</exception>
    /// <remarks>A handle that has not found a live server yet discovers one first.</remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> GetWindowAsync(
        WindowId id,
        CancellationToken cancellationToken = default) =>
        await FindWindowAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new TmuxObjectNotFoundException($"Window {id} was not found.", id.ToString());

    /// <summary>Reads one window by identifier, returning null when it is absent.</summary>
    /// <param name="id">The window identifier.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A materialized window, or null after a successful read finds no match.</returns>
    /// <exception cref="LibTmuxException">The lookup failed, including an absent daemon.</exception>
    /// <remarks>A handle that has not found a live server yet discovers one first.</remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window?> FindWindowAsync(
        WindowId id,
        CancellationToken cancellationToken = default)
    {
        Server owner = await ListingOwnerAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string?>? row = await RelationReader.FindAsync(
                owner,
                "list-windows",
                "window_id",
                id.ToString(),
                inSession: null,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : RelationReader.ToWindow(owner, row);
    }

    /// <summary>Reads one pane by identifier, throwing when it is absent.</summary>
    /// <param name="id">The pane identifier.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A materialized pane carrying captured scalar state.</returns>
    /// <exception cref="TmuxObjectNotFoundException">The pane does not exist.</exception>
    /// <exception cref="LibTmuxException">The lookup failed, including an absent daemon.</exception>
    /// <remarks>A handle that has not found a live server yet discovers one first.</remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> GetPaneAsync(
        PaneId id,
        CancellationToken cancellationToken = default) =>
        await FindPaneAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new TmuxObjectNotFoundException($"Pane {id} was not found.", id.ToString());

    /// <summary>Reads one pane by identifier, returning null when it is absent.</summary>
    /// <param name="id">The pane identifier.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A materialized pane, or null after a successful read finds no match.</returns>
    /// <exception cref="LibTmuxException">The lookup failed, including an absent daemon.</exception>
    /// <remarks>A handle that has not found a live server yet discovers one first.</remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane?> FindPaneAsync(
        PaneId id,
        CancellationToken cancellationToken = default)
    {
        Server owner = await ListingOwnerAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string?>? row = await RelationReader.FindAsync(
                owner,
                "list-panes",
                "pane_id",
                id.ToString(),
                inSession: null,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : RelationReader.ToPane(owner, row);
    }

    /// <summary>Reports whether two handles reach the same server endpoint.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true" /> when both reach the same endpoint.</returns>
    public static bool operator ==(Server? left, Server? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Reports whether two handles reach different server endpoints.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true" /> when they do not reach the same endpoint.</returns>
    public static bool operator !=(Server? left, Server? right) => !(left == right);

    /// <inheritdoc />
    /// <remarks>
    /// A handle that has not reached tmux has no endpoint to compare, so it
    /// equals only itself.
    /// </remarks>
    public bool Equals(Server? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && _connection is not null
            && other._connection is not null
            && _connection.HasSameEndpoint(other._connection));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Server);

    /// <inheritdoc />
    public override int GetHashCode() =>
        _connection is null
            ? base.GetHashCode()
            : _connection.GetEndpointHashCode();
}
