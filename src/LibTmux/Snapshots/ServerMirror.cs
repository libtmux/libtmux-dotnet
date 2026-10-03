using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text;

namespace LibTmux;

/// <summary>One state of a mirrored server.</summary>
/// <param name="Epoch">How many views the mirror published before this one.</param>
/// <param name="Server">The server as the rebuild captured it, down to its panes.</param>
/// <param name="Clients">The clients attached when it was captured.</param>
public sealed record ServerMirrorView(long Epoch, Server Server, IReadOnlyList<Client> Clients);

/// <summary>
/// A live copy of one tmux server's sessions, windows, panes and clients, rebuilt
/// whenever tmux announces a change.
/// </summary>
/// <remarks>
/// <para>
/// A control client attached to an anchor session hears what tmux announces. Any
/// announcement, or the loss of one, starts a fresh capture, never a patch of the
/// last one, since an announcement says that something changed, not everything
/// that did. Announcements that arrive during a capture collapse into one more,
/// and a capture that finds nothing different publishes nothing; activity
/// times, cursor positions and history sizes, which change with every
/// keystroke, do not count as different. Each published view carries an epoch
/// one above the last, counted by this mirror.
/// </para>
/// <para>
/// tmux announces only some changes to a client: not a pane's running command or
/// working directory, and not a layout change in a session the client is not
/// attached to. Give a refresh interval to see those within that long.
/// </para>
/// <para>
/// When the control client ends, the mirror finds the anchor session again by its
/// ID and attaches through it; if the session has gone, the mirror ends with
/// <see cref="TmuxObjectNotFoundException" />, and if the server has, with the
/// failure that said so. The client asks tmux for notifications only, not pane
/// output, and does not affect window sizes.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class ServerMirror : IAsyncDisposable
{
    // Fields that move with every keystroke, line of output or redrawn frame,
    // or with the clock: client_activity_string changes each second a client
    // is used, client_written with every status-line redraw, the window
    // offsets with the cursor of a window larger than its client, and a
    // program updating its screen atomically toggles synchronized_output_flag
    // around every frame.
    private static readonly FrozenSet<string> Restless = FrozenSet.ToFrozenSet(
        [
            "client_activity", "client_activity_string", "client_discarded", "client_written",
            "cursor_character", "cursor_x", "cursor_y", "history_bytes", "history_size",
            "saved_cursor_x", "saved_cursor_y", "session_activity", "synchronized_output_flag",
            "window_activity", "window_offset_x", "window_offset_y",
        ],
        StringComparer.Ordinal);

    private static readonly TimeSpan FirstReattachDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LongestReattachDelay = TimeSpan.FromSeconds(5);
    private const int AttachAttempts = 5;

    private readonly Server _server;
    private readonly SessionId _anchor;
    private readonly TimeSpan _refreshEvery;
    private readonly CancellationTokenSource _closing = new();
    private readonly object _gate = new();
    private ServerMirrorView _current;
    private string _fingerprint;
    private TaskCompletionSource _published = NewSignal();
    private TaskCompletionSource _wake = NewSignal();
    private IControlModeSession _control;

    // Set once the listener has taken a client whose events ended to dispose
    // it, so the mirror's own disposal does not dispose it again.
    private bool _controlReleased;
    private bool _dirty;
    private bool _ended;
    private Exception? _failure;
    private readonly Task _listening;
    private readonly Task _rebuilding;

    private ServerMirror(
        Server server,
        SessionId anchor,
        TimeSpan refreshEvery,
        IControlModeSession control,
        ServerMirrorView first)
    {
        _server = server;
        _anchor = anchor;
        _refreshEvery = refreshEvery;
        _control = control;
        _current = first;
        _fingerprint = Fingerprint(first.Server, first.Clients);
        _listening = Task.Run(ListenAsync);
        _rebuilding = Task.Run(RebuildAsync);
    }

    /// <summary>Gets the latest published view.</summary>
    public ServerMirrorView Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Gets whether the mirror has stopped publishing, because it was disposed or its anchor has gone.</summary>
    public bool IsEnded
    {
        get
        {
            lock (_gate)
            {
                return _ended;
            }
        }
    }

    /// <summary>Gets why the mirror ended, when something other than disposal ended it.</summary>
    public Exception? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    /// <summary>Mirrors the server an anchor session belongs to.</summary>
    /// <param name="anchor">The session the mirror's control client attaches to.</param>
    /// <param name="refreshEvery">
    /// How long a quiet mirror waits before capturing anyway, so a change tmux
    /// does not announce is seen within that long; zero never does.
    /// </param>
    /// <param name="cancellationToken">Cancels attaching and the first capture.</param>
    /// <returns>A mirror whose first view is already captured.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The refresh interval is negative.</exception>
    /// <exception cref="IncompleteSnapshotException">The session was not read through a server.</exception>
    public static async Task<ServerMirror> OpenAsync(
        Session anchor,
        TimeSpan refreshEvery = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentOutOfRangeException.ThrowIfLessThan(refreshEvery, TimeSpan.Zero);
        Server server = anchor.Server;
        IControlModeSession control = await AttachAsync(server, anchor.Id, cancellationToken).ConfigureAwait(false);
        try
        {
            (Server captured, IReadOnlyList<Client> clients) = await CaptureAsync(server, cancellationToken)
                .ConfigureAwait(false);
            return new ServerMirror(server, anchor.Id, refreshEvery, control, new ServerMirrorView(0, captured, clients));
        }
        catch
        {
            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Waits for a view newer than an epoch.</summary>
    /// <param name="epoch">The epoch of the view the caller already has.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The newer view, or null once the mirror has ended without one.</returns>
    public async Task<ServerMirrorView?> WaitForNewerAsync(long epoch, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task published;
            lock (_gate)
            {
                if (_current.Epoch > epoch)
                {
                    return _current;
                }

                if (_ended)
                {
                    return null;
                }

                published = _published.Task;
            }

            await published.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until a view satisfies a condition, testing the current view first.</summary>
    /// <param name="condition">Judges each view.</param>
    /// <param name="timeout">How long to wait, or <see cref="Timeout.InfiniteTimeSpan" />.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The first view the condition accepts.</returns>
    /// <exception cref="TmuxWaitTimeoutException">No view satisfied the condition in time.</exception>
    /// <exception cref="InvalidOperationException">The mirror ended first; its failure, if any, is the inner exception.</exception>
    public async Task<ServerMirrorView> WaitUntilAsync(
        Func<ServerMirrorView, bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        ServerMirrorView view = Current;
        try
        {
            while (!condition(view))
            {
                view = await WaitForNewerAsync(view.Epoch, deadline.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The mirror ended before the condition held.", Failure);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TmuxWaitTimeoutException("No mirrored view satisfied the condition in time.", timeout);
        }

        return view;
    }

    /// <summary>Streams the current view and then each newer one, skipping any published while the reader was busy.</summary>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <returns>Views in epoch order, ending when the mirror ends; a failure that ended it is thrown.</returns>
    public async IAsyncEnumerable<ServerMirrorView> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ServerMirrorView? view = Current;
        while (view is not null)
        {
            yield return view;
            view = await WaitForNewerAsync(view.Epoch, cancellationToken).ConfigureAwait(false);
        }

        if (Failure is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>Stops listening and detaches the control client; the last view stays readable.</summary>
    /// <returns>A task that completes when the mirror has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        End(null);
        try
        {
            await Task.WhenAll(_listening, _rebuilding).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Both loops report their failures through End.
        }

        IControlModeSession? control;
        lock (_gate)
        {
            control = _controlReleased ? null : _control;
        }

        if (control is not null)
        {
            await control.DisposeAsync().ConfigureAwait(false);
        }

        _closing.Dispose();
    }

    private static async Task<IControlModeSession> AttachAsync(
        Server server,
        SessionId anchor,
        CancellationToken cancellationToken)
    {
        IControlModeSession control = await server
            .EnterControlModeAsync(anchor.ToString(), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await control
                .SendAsync(TmuxCommand.Create("refresh-client", "-f", "no-output,ignore-size"), cancellationToken)
                .ConfigureAwait(false);
            return control;
        }
        catch
        {
            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<(Server Server, IReadOnlyList<Client> Clients)> CaptureAsync(
        Server server,
        CancellationToken cancellationToken)
    {
        Server captured = await server
            .CaptureSnapshotAsync(SnapshotDepth.Panes, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<Client> clients = await server.GetClientsAsync(cancellationToken).ConfigureAwait(false);
        return (captured, clients);
    }

    private async Task ListenAsync()
    {
        CancellationToken closing = _closing.Token;
        string anchor = _anchor.ToString();
        TimeSpan reattachDelay = FirstReattachDelay;
        try
        {
            while (true)
            {
                IControlModeSession control;
                lock (_gate)
                {
                    control = _control;
                }

                long listening = Stopwatch.GetTimestamp();
                try
                {
                    await foreach (TmuxEvent item in control.Events.WithCancellation(closing).ConfigureAwait(false))
                    {
                        // With detach-on-destroy off, tmux moves a client whose
                        // session ends to another session rather than ending it.
                        if (item is TmuxNotificationEvent { Name: "session-changed", Arguments: [string session, ..] }
                            && !string.Equals(session, anchor, StringComparison.Ordinal))
                        {
                            break;
                        }

                        if (item is TmuxNotificationEvent or TmuxEventsDroppedEvent { OnlyOutput: false })
                        {
                            RequestCapture();
                        }
                    }
                }
                catch (Exception) when (!closing.IsCancellationRequested)
                {
                    // A client killed without %exit faults its stream, though
                    // the server and the anchor may be alive; the lookup below
                    // reports a server that is not.
                }

                // The client ended or left the anchor, and tmux has forgotten
                // what it announced. Disposing a faulted client raises its
                // fault again.
                lock (_gate)
                {
                    _controlReleased = true;
                }

                try
                {
                    await control.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception) when (!closing.IsCancellationRequested)
                {
                }
                if (Stopwatch.GetElapsedTime(listening) < LongestReattachDelay)
                {
                    await Task.Delay(reattachDelay, closing).ConfigureAwait(false);
                    reattachDelay = reattachDelay * 2 < LongestReattachDelay ? reattachDelay * 2 : LongestReattachDelay;
                }
                else
                {
                    reattachDelay = FirstReattachDelay;
                }

                IControlModeSession? attached = null;
                for (int attempt = 1; attached is null; attempt++)
                {
                    if (await _server.FindSessionAsync(_anchor, closing).ConfigureAwait(false) is null)
                    {
                        End(new TmuxObjectNotFoundException(
                            $"The mirror's anchor session {_anchor} is gone.",
                            _anchor.ToString()));
                        return;
                    }

                    try
                    {
                        attached = await AttachAsync(_server, _anchor, closing).ConfigureAwait(false);
                    }
                    catch (Exception) when (attempt < AttachAttempts && !closing.IsCancellationRequested)
                    {
                        // The anchor was there a moment ago, so a failed attach
                        // may pass; one that keeps failing ends the mirror.
                        await Task.Delay(reattachDelay, closing).ConfigureAwait(false);
                        reattachDelay = reattachDelay * 2 < LongestReattachDelay ? reattachDelay * 2 : LongestReattachDelay;
                    }
                }
                lock (_gate)
                {
                    _control = attached;
                    _controlReleased = false;
                }

                RequestCapture();
            }
        }
        catch (Exception) when (closing.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            End(error);
        }
    }

    private async Task RebuildAsync()
    {
        CancellationToken closing = _closing.Token;
        try
        {
            while (true)
            {
                Task? wake = null;
                lock (_gate)
                {
                    if (_ended)
                    {
                        return;
                    }

                    if (!_dirty)
                    {
                        wake = _wake.Task;
                    }

                    _dirty = false;
                }

                if (wake is not null && !await WakeAsync(wake, closing).ConfigureAwait(false))
                {
                    continue;
                }

                await PublishAsync(closing).ConfigureAwait(false);
            }
        }
        catch (Exception) when (closing.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            End(error);
        }
    }

    // Answers whether to capture now: a refresh interval elapsed, or an
    // announcement arrived and should be taken up by the next turn.
    private async Task<bool> WakeAsync(Task wake, CancellationToken closing)
    {
        if (_refreshEvery == TimeSpan.Zero)
        {
            await wake.WaitAsync(closing).ConfigureAwait(false);
            return false;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(closing);
        Task refresh = Task.Delay(_refreshEvery, timer.Token);
        if (await Task.WhenAny(wake, refresh).ConfigureAwait(false) == refresh)
        {
            await refresh.ConfigureAwait(false);
            return true;
        }

        await timer.CancelAsync().ConfigureAwait(false);
        return false;
    }

    private async Task PublishAsync(CancellationToken closing)
    {
        Server captured;
        IReadOnlyList<Client> clients;
        try
        {
            (captured, clients) = await CaptureAsync(_server, closing).ConfigureAwait(false);
        }
        catch (InconsistentSnapshotException)
        {
            // tmux changed between the capture's reads; that change was
            // announced too, so capture again.
            RequestCapture();
            return;
        }

        string fingerprint = Fingerprint(captured, clients);
        TaskCompletionSource published;
        lock (_gate)
        {
            if (_ended || string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            _fingerprint = fingerprint;
            _current = new ServerMirrorView(_current.Epoch + 1, captured, clients);
            published = _published;
            _published = NewSignal();
        }

        published.TrySetResult();
    }

    private void RequestCapture()
    {
        TaskCompletionSource wake;
        lock (_gate)
        {
            _dirty = true;
            wake = _wake;
            _wake = NewSignal();
        }

        wake.TrySetResult();
    }

    private void End(Exception? failure)
    {
        TaskCompletionSource published;
        TaskCompletionSource wake;
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            _failure = failure;
            published = _published;
            wake = _wake;
        }

        _closing.Cancel();
        published.TrySetResult();
        wake.TrySetResult();
    }

    // Every captured field, so any difference publishes and none does not.
    private static string Fingerprint(Server server, IReadOnlyList<Client> clients)
    {
        var text = new StringBuilder();
        foreach (Session session in server.Sessions)
        {
            Append(text, 's', session.RawFormatFields);
            foreach (Window window in session.Windows)
            {
                Append(text, 'w', window.RawFormatFields);
                foreach (Pane pane in window.Panes)
                {
                    Append(text, 'p', pane.RawFormatFields);
                }
            }
        }

        foreach (Client client in clients)
        {
            Append(text, 'c', client.RawFormatFields);
        }

        return text.ToString();
    }

    internal static void Append(StringBuilder text, char kind, IReadOnlyDictionary<string, string?> fields)
    {
        text.Append(kind);
        foreach (KeyValuePair<string, string?> field in fields
            .Where(field => !Restless.Contains(field.Key))
            .OrderBy(field => field.Key, StringComparer.Ordinal))
        {
            text.Append('\u001f').Append(field.Key).Append('=').Append(field.Value);
        }

        text.Append('\u001e');
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
