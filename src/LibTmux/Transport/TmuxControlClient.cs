using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace LibTmux.Internal;

/// <summary>The control client this library holds on one server, and what tmux calls it.</summary>
/// <param name="Name">The client's <c>client_name</c>.</param>
/// <param name="SessionId">The session it is attached to, as <c>$N</c>.</param>
internal sealed record OwnControlClient(string Name, string SessionId);

/// <summary>Holds the one long-lived control client every connection to an endpoint shares.</summary>
/// <remarks>
/// <para>
/// The client is how one-shot commands reach tmux without starting a process
/// each. It is attached to a session, so tmux counts it among that session's
/// clients and lists it; <see cref="Own" /> says which one it is so a listing
/// can leave it out. Every connection to the same endpoint shares it, since a
/// second client on one server would be the first client's visible neighbour.
/// </para>
/// <para>
/// It starts when a command first needs it, and only against a server that has
/// a session to attach to. It ends when its session or the server does; the
/// next command starts it again, or runs on a process when no session remains.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
#pragma warning disable CA1001 // Lives as long as the process; the client ends with its server.
internal sealed class TmuxControlClient
#pragma warning restore CA1001
{
    private static readonly ConcurrentDictionary<string, TmuxControlClient> Registry = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ControlModeSession? _session;
    private volatile OwnControlClient? _own;
    private volatile bool _blocked;
    private string? _configuredPath;
    private volatile string? _watchedPath;
    private (ulong Device, ulong Inode)? _watchedIdentity;
    private volatile DaemonVerdict? _daemon;

    private sealed record DaemonVerdict((ulong Device, ulong Inode)? Socket, bool Safe);

    /// <summary>Gets the shared client for an endpoint, creating it on first use.</summary>
    /// <param name="endpoint">The endpoint's fingerprint.</param>
    /// <returns>The client holder.</returns>
    /// <param name="socketPath">The socket path the connection was given, when it was given one.</param>
    internal static TmuxControlClient For(string endpoint, string? socketPath = null)
    {
        TmuxControlClient client = Registry.GetOrAdd(endpoint, static _ => new TmuxControlClient());
        client._configuredPath ??= socketPath;
        return client;
    }

    /// <summary>Gets the client holder for an endpoint, or null when none was ever created.</summary>
    /// <param name="endpoint">The endpoint's fingerprint.</param>
    /// <returns>The client holder, or null.</returns>
    internal static TmuxControlClient? Find(string endpoint) =>
        Registry.TryGetValue(endpoint, out TmuxControlClient? client) ? client : null;

    /// <summary>Gets the running client's identity, or null when none is attached.</summary>
    internal OwnControlClient? Own => _session is { IsRunning: true } ? _own : null;

    /// <summary>Notes that a session may exist, so a failed attach is worth trying again.</summary>
    internal void MayHaveSession() => _blocked = false;

    /// <summary>Returns the running client, starting one if a session can be attached.</summary>
    /// <param name="start">Starts a client process.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The client, or null when the server has no session to attach to.</returns>
    internal async Task<ControlModeSession?> GetAsync(
        Func<ControlModeSession> start,
        CancellationToken cancellationToken)
    {
        if (_session is { IsRunning: true } running)
        {
            if (NamesTheSocketItAttachedTo())
            {
                return running;
            }

            // The path names another daemon now. This command goes to a
            // process, which reaches the daemon the path names and lets the
            // generation guard say so; the next one attaches to that daemon.
            await ReleaseAsync(running).ConfigureAwait(false);
            return null;
        }

        if (_blocked)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is { IsRunning: true } again)
            {
                return again;
            }

            if (_blocked)
            {
                return null;
            }

            if (_session is not null)
            {
                _ = _session.DisposeAsync().AsTask();
                _session = null;
                _own = null;
            }

            ControlModeSession started = start();
            try
            {
                await started.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
                (OwnControlClient own, string reportedPath) = await IdentifyAsync(started, cancellationToken)
                    .ConfigureAwait(false);
                _watchedPath = _configuredPath ?? reportedPath;
                _watchedIdentity = UnixFileIdentity.TryRead(_watchedPath);
                _own = own;
                _session = started;
                _ = Task.Run(() => FollowAsync(started, own), CancellationToken.None);
                return started;
            }
            catch (Exception)
            {
                // A server with no session to attach to answers the attach with
                // an error. That is not a failure of the command: it runs on a
                // process, which says what tmux says.
                _ = started.DisposeAsync().AsTask();
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                _blocked = true;
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Says whether the server on the endpoint is at least a version, asking it once per server.</summary>
    /// <param name="read">Reads the server's version without a control client; null when it cannot be read.</param>
    /// <param name="floor">The oldest version the client may attach to.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True when the client may attach; false when the server is older or cannot be read.</returns>
    /// <remarks>
    /// A running client was attached to a server that passed. The answer is kept
    /// for as long as the socket path names the same file; a new server makes a
    /// new socket, so it is asked again. An unreadable version is not kept, since
    /// it usually means no server is running yet.
    /// </remarks>
    internal async Task<bool> DaemonIsAtLeastAsync(
        Func<CancellationToken, Task<TmuxVersion?>> read,
        TmuxVersion floor,
        CancellationToken cancellationToken)
    {
        if (_session is { IsRunning: true })
        {
            return true;
        }

        string? path = _watchedPath ?? _configuredPath;
        (ulong Device, ulong Inode)? socket = path is null ? null : UnixFileIdentity.TryRead(path);
        if (socket is not null && _daemon is { } known && known.Socket == socket)
        {
            return known.Safe;
        }

        if (await read(cancellationToken).ConfigureAwait(false) is not { } version)
        {
            return false;
        }

        bool safe = version >= floor;
        if (socket is not null)
        {
            _daemon = new DaemonVerdict(socket, safe);
        }

        return safe;
    }

    /// <summary>Waits until the client has either answered a question or ended.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the client's state is settled.</returns>
    internal async Task SettleAsync(CancellationToken cancellationToken)
    {
        if (_session is not { IsRunning: true } running)
        {
            return;
        }

        try
        {
            _ = await running
                .SendRawAsync([["display-message", "-p", string.Empty]], new ControlModeSendProbe(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // It ended with what it was attached to; that is the answer.
        }
    }

    // One system call: the path is read again and must name the file it did
    // when the client attached. A client that cannot tell checks nothing.
    private bool NamesTheSocketItAttachedTo() =>
        _watchedIdentity is not { } attached
        || _watchedPath is not { } path
        || UnixFileIdentity.TryRead(path) == attached;

    private async Task ReleaseAsync(ControlModeSession session)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
                _own = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // It is being abandoned either way.
        }
    }

    private static async Task<(OwnControlClient Own, string SocketPath)> IdentifyAsync(
        ControlModeSession session,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> lines = await session
            .SendRawAsync(
                [["display-message", "-p", "#{client_name}\t#{session_id}\t#{socket_path}"]],
                new ControlModeSendProbe(),
                cancellationToken)
            .ConfigureAwait(false);
        string[] parts = lines.Count == 1 ? lines[0].Split('\t') : [];
        return parts.Length == 3 && parts[0].Length > 0 && parts[1].Length > 0 && parts[2].Length > 0
            ? (new OwnControlClient(parts[0], parts[1]), parts[2])
            : throw new TmuxProtocolException(
                "tmux did not name the control client.",
                TmuxDispatchState.Dispatched);
    }

    // The client moves to another session when its own is destroyed on a server
    // that keeps detached clients; tmux says so as a notification. Reading the
    // events also keeps the client's buffer from filling.
    private async Task FollowAsync(ControlModeSession session, OwnControlClient first)
    {
        try
        {
            await foreach (TmuxEvent tmuxEvent in session.Events.ConfigureAwait(false))
            {
                if (tmuxEvent is TmuxNotificationEvent { Name: "session-changed", Arguments.Count: > 0 } changed
                    && ReferenceEquals(_session, session))
                {
                    _own = first with { SessionId = changed.Arguments[0] };
                }
            }
        }
        catch (Exception)
        {
            // The client is gone either way; the next command starts another.
        }
        finally
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Nothing is left to report to.
            }
        }
    }
}
