using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <summary>Holds the tmux servers this process talks to, one per socket.</summary>
/// <remarks>
/// <para>
/// A conversation outlives any single tool call, so connecting once and keeping
/// the handle costs one process launch instead of one per call. Sockets are
/// cached separately because a machine runs several tmux servers at once — a
/// user's own, a test rig's, a sandbox's — and they are different servers with
/// the same commands, not one server addressed two ways.
/// </para>
/// <para>
/// When no template is supplied, the tmux binary comes from
/// <c>LIBTMUX_TMUX</c>. An explicit template is already a pinned route and wins
/// over later process-environment changes.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class TmuxConnectionAccessor : IDisposable
{
    private const string BinaryVariable = "LIBTMUX_TMUX";

    private readonly ConcurrentDictionary<string, Lazy<Task<Server>>> _servers = new(StringComparer.Ordinal);
    private Server? _fixed;
    private readonly ServerConnectionOptions _template;
    private readonly string _binaryPath;
    private readonly string? _defaultSocketName;
    private readonly ILogger? _logger;

    /// <summary>Initializes the accessor.</summary>
    /// <param name="template">How to reach tmux, or null for the ambient server.</param>
    /// <param name="defaultSocketName">The socket used when a call names none.</param>
    /// <param name="logger">Records connection failures.</param>
    public TmuxConnectionAccessor(
        ServerConnectionOptions? template = null,
        string? defaultSocketName = null,
        ILogger? logger = null)
    {
        bool useProcessDefault = template is null;
        _template = template ?? new ServerConnectionOptions();
        _binaryPath = useProcessDefault
            && System.Environment.GetEnvironmentVariable(BinaryVariable) is string named
            && !string.IsNullOrWhiteSpace(named)
                ? named
                : _template.TmuxBinaryPath;
        _defaultSocketName = defaultSocketName ?? _template.SocketName;
        _logger = logger;
    }

    /// <summary>Initializes the accessor over a server that is already connected.</summary>
    /// <param name="server">The server every call reaches.</param>
    /// <remarks>
    /// For an application that already holds a connection. It reuses that one
    /// rather than opening a second, and — more importantly — never resolves a
    /// socket from the environment, which is what a caller wants when the
    /// environment is not the thing that decided which server this is.
    /// </remarks>
    public TmuxConnectionAccessor(Server server)
        : this(server?.ConnectionOptions, server?.ConnectionOptions.SocketName)
    {
        ArgumentNullException.ThrowIfNull(server);
        _fixed = server;
    }

    /// <summary>Gets the socket used when a call names none.</summary>
    public string? DefaultSocketName => _defaultSocketName;

    /// <inheritdoc />
    public void Dispose() => _servers.Clear();

    /// <summary>Answers a server, connecting on the first ask for that socket.</summary>
    /// <param name="socketName">The socket, or null for the configured default.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The server.</returns>
    /// <exception cref="McpException">tmux could not be reached.</exception>
    public async Task<Server> GetAsync(
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        // A server handed in wins over a resolved name: the caller decided.
        // It materializes here because a handle can be valid before reading
        // its own version and generation, which downstream calls need.
        if (_fixed is Server held && string.IsNullOrWhiteSpace(socketName))
        {
            if (held.IsMaterialized)
            {
                return held;
            }

            Server materialized = await held.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _fixed = materialized;
            return materialized;
        }

        string? resolved = string.IsNullOrWhiteSpace(socketName)
            ? _defaultSocketName
            : socketName.Trim();
        string key = resolved ?? string.Empty;

        // Lazy rather than GetOrAdd of a started task: two calls arriving
        // together would otherwise both launch tmux, and one of them would end
        // up holding a handle nothing else knows about.
        Lazy<Task<Server>> entry = _servers.GetOrAdd(
            key,
            _ => new Lazy<Task<Server>>(
                () => Server.ConnectAsync(OptionsFor(resolved), CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TmuxCommandNotFoundException error)
        {
            _servers.TryRemove(key, out _);
            throw new McpException(
                $"No tmux binary was found. Install tmux, or set {BinaryVariable} to one. ({error.Message})");
        }
        catch (LibTmuxException error)
        {
            // Connecting asks a server to identify itself, so this fails when
            // nothing is listening yet — normal before the first session, and
            // the handle returned below can still create one.
            _servers.TryRemove(key, out _);
            if (_logger is not null)
            {
                Log.ServerUnreachable(_logger, error, resolved);
            }

            return Server.Open(OptionsFor(resolved));
        }
    }

    /// <summary>Forgets a cached server so the next call reconnects.</summary>
    /// <param name="socketName">The socket, or null for the configured default.</param>
    /// <remarks>
    /// A materialized handle names one tmux <em>process</em>. Killing the
    /// server and starting another gives the same socket a different process,
    /// and the old handle then refuses to be used rather than quietly
    /// answering for a server that no longer exists.
    /// </remarks>
    public void Invalidate(string? socketName = null)
    {
        string key = string.IsNullOrWhiteSpace(socketName)
            ? _defaultSocketName ?? string.Empty
            : socketName.Trim();
        _servers.TryRemove(key, out _);
    }

    // Rebuilt rather than copied: ServerConnectionOptions exposes its
    // properties get-only, so `with` cannot reach the socket.
    private ServerConnectionOptions OptionsFor(string? socketName) => new(
        tmuxBinaryPath: _binaryPath,
        socketName: socketName,
        socketPath: socketName is null ? _template.SocketPath : null,
        socketNameFactory: socketName is null ? _template.SocketNameFactory : null,
        configurationFile: _template.ConfigurationFile,
        colorMode: _template.ColorMode,
        initializeAsync: _template.InitializeAsync,
        childEnvironment: _template.ChildEnvironment,
        logger: _template.Logger);
}
