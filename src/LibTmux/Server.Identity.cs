using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Provides server connection identity and typed lookup.
public sealed partial class Server
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

    /// <summary>Rejects a lookup answered by a server this handle is not on.</summary>
    /// <remarks>
    /// The identifier is resolved against the running daemon while the handle
    /// carries the generation it was discovered at. A replacement server hands
    /// out the same identifiers, so a handle built from both would name the new
    /// server's object while reporting the old server as its owner.
    /// </remarks>
    private void RequireOwnedGeneration(ServerGeneration observed)
    {
        ServerGeneration expected = _generation
            ?? throw new InvalidOperationException("The server has no live generation.");
        if (observed != expected)
        {
            throw new StaleServerGenerationException(
                "The tmux server generation changed before the lookup answered.",
                expected,
                observed);
        }
    }

    /// <summary>Gets one session by its typed identifier.</summary>
    [UnsupportedOSPlatform("windows")]
    public async Task<Session> GetSessionAsync(
        SessionId id,
        CancellationToken cancellationToken = default)
    {
        TmuxConnection connection = RequireMaterializedConnection();
        (ServerGeneration Generation, SessionId Id)? identity = await connection
            .FindSessionAsync(id, cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            throw new TmuxObjectNotFoundException($"Session {id} was not found.", id.ToString());
        }

        RequireOwnedGeneration(identity.Value.Generation);
        return new Session(this, connection, identity.Value.Generation, identity.Value.Id);
    }

    /// <summary>Gets one window by its typed identifier.</summary>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> GetWindowAsync(
        WindowId id,
        CancellationToken cancellationToken = default)
    {
        TmuxConnection connection = RequireMaterializedConnection();
        (ServerGeneration Generation, WindowId Id)? identity = await connection
            .FindWindowAsync(id, cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            throw new TmuxObjectNotFoundException($"Window {id} was not found.", id.ToString());
        }

        RequireOwnedGeneration(identity.Value.Generation);
        return new Window(this, connection, identity.Value.Generation, identity.Value.Id);
    }

    /// <summary>Gets one pane by its typed identifier.</summary>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> GetPaneAsync(
        PaneId id,
        CancellationToken cancellationToken = default)
    {
        TmuxConnection connection = RequireMaterializedConnection();
        (ServerGeneration Generation, PaneId Id)? identity = await connection
            .FindPaneAsync(id, cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            throw new TmuxObjectNotFoundException($"Pane {id} was not found.", id.ToString());
        }

        RequireOwnedGeneration(identity.Value.Generation);
        return new Pane(this, connection, identity.Value.Generation, identity.Value.Id);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        return obj is Server other
            && _connection is not null
            && other._connection is not null
            && _connection.HasSameEndpoint(other._connection);
    }

    /// <inheritdoc />
    public override int GetHashCode() =>
        _connection is null
            ? base.GetHashCode()
            : _connection.GetEndpointHashCode();

    private TmuxConnection RequireMaterializedConnection()
    {
        if (_connection is null || !_generation.HasValue)
        {
            throw new InvalidOperationException("The server must be materialized before lookup.");
        }

        return _connection;
    }
}
