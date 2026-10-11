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

    /// <summary>Gets the shared client for an endpoint, creating it on first use.</summary>
    /// <param name="endpoint">The endpoint's fingerprint.</param>
    /// <returns>The client holder.</returns>
    internal static TmuxControlClient For(string endpoint) =>
        Registry.GetOrAdd(endpoint, static _ => new TmuxControlClient());

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
            return running;
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
                OwnControlClient own = await IdentifyAsync(started, cancellationToken).ConfigureAwait(false);
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

    private static async Task<OwnControlClient> IdentifyAsync(
        ControlModeSession session,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> lines = await session
            .SendRawAsync(
                [["display-message", "-p", "#{client_name}\t#{session_id}"]],
                new ControlModeSendProbe(),
                cancellationToken)
            .ConfigureAwait(false);
        string[] parts = lines.Count == 1 ? lines[0].Split('\t') : [];
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0
            ? new OwnControlClient(parts[0], parts[1])
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
