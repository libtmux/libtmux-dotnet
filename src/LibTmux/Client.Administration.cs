using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>Identifies a client and resolves what it is looking at.</summary>
/// <remarks>
/// A client moves between sessions while a handle is held, so the captured
/// fields say where it was and the resolving methods say where it is. Both are
/// useful, and conflating them would make one of them a lie.
/// </remarks>
public sealed partial class Client
{
    private readonly Server? _owner;
    private readonly TmuxCommandDispatcher _commandDispatcher;
    private readonly ServerGeneration _generation;
    private readonly IReadOnlyDictionary<string, string?> _snapshot;

    [UnsupportedOSPlatform("windows")]
    internal Client(
        Server owner,
        TmuxConnection connection,
        ServerGeneration generation,
        IReadOnlyDictionary<string, string?> snapshot)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(snapshot);
        _owner = owner;
        _commandDispatcher = connection.CreateEntityDispatcher(generation);
        _generation = generation;
        _snapshot = snapshot;
    }

    /// <summary>Gets the client name tmux knows it by.</summary>
    public string Name =>
        ReadSnapshot("client_name")
        ?? throw new IncompleteSnapshotException("name", SnapshotDepth.Server);

    /// <summary>Gets the terminal the client is on, when it has one.</summary>
    public string? Tty => ReadSnapshot("client_tty");

    /// <summary>Gets whether the client speaks tmux's control protocol.</summary>
    public bool IsControlClient => ReadSnapshot("client_control_mode") == "1";

    /// <summary>Gets the session the client was attached to when it was read.</summary>
    /// <remarks>
    /// This is captured state. Use <see cref="GetAttachedSessionAsync" /> for
    /// where the client is now.
    /// </remarks>
    public SessionId? AttachedSessionId =>
        SessionId.TryParse(ReadSnapshot("session_id"), out SessionId id) ? id : null;

    /// <summary>Gets the server generation captured with this client.</summary>
    public ServerGeneration Generation => _generation;

    /// <summary>Gets the tmux fields captured when this handle materialized.</summary>
    public IReadOnlyDictionary<string, string?> RawFormatFields => _snapshot;

    /// <summary>Gets the server that owns this client.</summary>
    public Server Server =>
        _owner ?? throw new IncompleteSnapshotException("server", SnapshotDepth.Server);

    /// <summary>Reads one client by name.</summary>
    /// <param name="server">The server to look on.</param>
    /// <param name="name">The client name.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The client.</returns>
    /// <exception cref="TmuxObjectNotFoundException">The server has no such client.</exception>
    [UnsupportedOSPlatform("windows")]
    public static async Task<Client> GetAsync(
        Server server,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        IReadOnlyList<Client> clients = await server.GetClientsAsync(cancellationToken)
            .ConfigureAwait(false);
        return clients.FirstOrDefault(client =>
                string.Equals(client.Name, name, StringComparison.Ordinal))
            ?? throw new TmuxObjectNotFoundException(
                $"tmux has no client named '{name}'.",
                name);
    }

    /// <summary>Re-reads this client from tmux.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying current state.</returns>
    /// <exception cref="TmuxObjectNotFoundException">The client has gone.</exception>
    [UnsupportedOSPlatform("windows")]
    public Task<Client> RefreshAsync(CancellationToken cancellationToken = default) =>
        GetAsync(Server, Name, cancellationToken);

    /// <summary>Reads where this client is looking now.</summary>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>
    /// The session, window and pane the client is on, or null when the client
    /// is gone or attached to nothing.
    /// </returns>
    /// <remarks>
    /// The three parts come from one reading of the client, so a client that
    /// moves between sessions cannot yield a window from one and a pane from
    /// another.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<ClientAttachment?> ResolveAttachmentAsync(
        CancellationToken cancellationToken = default)
    {
        Server owner = Server;
        IReadOnlyList<Client> clients = await owner.GetClientsAsync(cancellationToken)
            .ConfigureAwait(false);
        Client? live = clients.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, Name, StringComparison.Ordinal));
        if (live?.AttachedSessionId is not SessionId attached)
        {
            return null;
        }

        IReadOnlyList<Session> sessions = await owner.GetSessionsAsync(cancellationToken)
            .ConfigureAwait(false);
        Session? session = sessions.FirstOrDefault(candidate => candidate.Id == attached);
        if (session is null)
        {
            // The client named a session that has since gone; reporting the
            // stale identity would be worse than reporting nothing.
            return null;
        }

        IReadOnlyList<Window> windows = await session.GetWindowsAsync(cancellationToken)
            .ConfigureAwait(false);
        Window? window = windows.FirstOrDefault(
            candidate => candidate.Snapshot?["window_active"] == "1");
        if (window is null)
        {
            return new ClientAttachment(session, null, null);
        }

        IReadOnlyList<Pane> panes = await window.GetPanesAsync(cancellationToken)
            .ConfigureAwait(false);
        return new ClientAttachment(
            session,
            window,
            panes.FirstOrDefault(candidate => candidate.Snapshot?["pane_active"] == "1"));
    }

    /// <summary>Reads the session this client is attached to now.</summary>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The session, or null when the client is attached to none.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Session?> GetAttachedSessionAsync(
        CancellationToken cancellationToken = default) =>
        (await ResolveAttachmentAsync(cancellationToken).ConfigureAwait(false))?.Session;

    /// <summary>Reads the window this client is showing now.</summary>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The window, or null when the client is showing none.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window?> GetAttachedWindowAsync(
        CancellationToken cancellationToken = default) =>
        (await ResolveAttachmentAsync(cancellationToken).ConfigureAwait(false))?.Window;

    /// <summary>Reads the pane this client has active now.</summary>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The pane, or null when the client has none.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane?> GetAttachedPaneAsync(
        CancellationToken cancellationToken = default) =>
        (await ResolveAttachmentAsync(cancellationToken).ConfigureAwait(false))?.Pane;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is Client other
        && _generation == other._generation
        && string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(_generation, StringComparer.Ordinal.GetHashCode(Name));

    private string? ReadSnapshot(string wireName) =>
        _snapshot.TryGetValue(wireName, out string? value) ? value : null;
}
