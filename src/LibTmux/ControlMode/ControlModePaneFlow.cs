namespace LibTmux.Internal;

/// <summary>Pauses panes whose output a slow reader is losing, and resumes them once it catches up.</summary>
/// <remarks>
/// A pane that floods a control client fills its event buffer with output the
/// reader never sees. Pausing the pane for this client with
/// <c>refresh-client -A %N:pause</c> (tmux 3.2) stops tmux sending it at all,
/// and tmux reports <c>%pause</c>. Once the reader has drained the buffer to a
/// quarter of its capacity, every paused pane is resumed and tmux reports
/// <c>%continue</c>. Output a pane printed while paused is not sent; its screen
/// is still current.
/// </remarks>
internal sealed class ControlModePaneFlow
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly Func<TmuxCommand, Task> _send;
    private readonly int _capacity;
    private readonly int _resumeAt;
    private readonly object _gate = new();
    private readonly HashSet<PaneId> _toPause = [];
    private readonly HashSet<PaneId> _paused = [];
    private TaskCompletionSource _wake = NewSignal();
    private int _pending;
    private bool _resume;
    private bool _stopped;
    private Task? _worker;

    /// <summary>Initializes flow control for one control client.</summary>
    /// <param name="send">Sends a command through the client.</param>
    /// <param name="capacity">The capacity of the client's event buffer.</param>
    internal ControlModePaneFlow(Func<TmuxCommand, Task> send, int capacity)
    {
        _send = send;
        _capacity = capacity;
        _resumeAt = capacity / 4;
    }

    /// <summary>Notes that the full buffer discarded output from a pane.</summary>
    /// <remarks>Called under the buffer's lock, so it only records and signals.</remarks>
    internal void OutputDiscarded(PaneId pane)
    {
        TaskCompletionSource? wake;
        lock (_gate)
        {
            _pending = _capacity;
            if (_stopped || _paused.Contains(pane) || !_toPause.Add(pane))
            {
                return;
            }

            _worker ??= Task.Run(RunAsync);
            wake = TakeWake();
        }

        wake.TrySetResult();
    }

    /// <summary>Notes that the reader took an event, leaving the given count.</summary>
    /// <remarks>Called under the buffer's lock, so it only records and signals.</remarks>
    internal void Dequeued(int remaining)
    {
        TaskCompletionSource? wake;
        lock (_gate)
        {
            _pending = remaining;
            if (remaining > _resumeAt || _paused.Count == 0 || _resume)
            {
                return;
            }

            _resume = true;
            wake = TakeWake();
        }

        wake.TrySetResult();
    }

    /// <summary>Stops sending; panes paused for an ending client need nothing more.</summary>
    internal void Stop()
    {
        TaskCompletionSource wake;
        lock (_gate)
        {
            _stopped = true;
            wake = TakeWake();
        }

        wake.TrySetResult();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            Task? wait = null;
            PaneId[] pause;
            PaneId[] resume = [];
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                pause = [.. _toPause];
                if (_resume)
                {
                    _resume = false;
                    resume = [.. _paused];
                }

                if (pause.Length == 0 && resume.Length == 0)
                {
                    wait = _wake.Task;
                }
            }

            if (wait is not null)
            {
                await wait.ConfigureAwait(false);
                continue;
            }

            foreach (PaneId pane in pause)
            {
                bool paused = await TrySendAsync(pane, "pause").ConfigureAwait(false);
                lock (_gate)
                {
                    // Left unpaused on failure; its next discarded output asks again.
                    _toPause.Remove(pane);
                    if (paused)
                    {
                        _paused.Add(pane);
                    }
                }
            }

            bool failed = false;
            foreach (PaneId pane in resume)
            {
                if (await TrySendAsync(pane, "continue").ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        _paused.Remove(pane);
                    }
                }
                else
                {
                    failed = true;
                }
            }

            if (failed)
            {
                await Task.Delay(RetryDelay).ConfigureAwait(false);
            }

            // A reader that drained while a pause was in flight saw no paused
            // pane, and a failed resume needs another attempt. With no further
            // events nothing else would ask, and the pane would stay paused.
            lock (_gate)
            {
                if (_paused.Count > 0 && _pending <= _resumeAt)
                {
                    _resume = true;
                }
            }
        }
    }

    private async Task<bool> TrySendAsync(PaneId pane, string state)
    {
        try
        {
            await _send(TmuxCommand.Create("refresh-client", "-A", $"{pane}:{state}")).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            // The client is ending, or its command limit was reached: resumes
            // are retried, and a pane's next discarded output asks for a pause.
            return false;
        }
    }

    // Called under the gate.
    private TaskCompletionSource TakeWake()
    {
        TaskCompletionSource wake = _wake;
        _wake = NewSignal();
        return wake;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
