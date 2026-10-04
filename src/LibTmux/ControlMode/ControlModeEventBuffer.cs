using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;

namespace LibTmux.Internal;

internal enum ControlModeEventRead
{
    Item,
    Boundary,
    Completed,
}

/// <summary>Buffers notifications without allowing a slow consumer to stall commands.</summary>
/// <remarks>
/// When full it discards the oldest output of the pane with the most queued
/// output events or bytes, according to the limit reached. Only a buffer
/// holding no output discards its oldest notification.
/// </remarks>
internal sealed class ControlModeEventBuffer
{
    internal const int DefaultMaxBytes = 4 * 1024 * 1024;

    private readonly int _capacity;
    private readonly int _maxBytes;
    private long _bufferedBytes;
    private readonly Action<int>? _afterDequeue;
    private readonly Action<PaneId>? _outputDiscarded;
    private readonly object _gate = new();
    private readonly LinkedList<(long Sequence, TmuxEvent Item, long Bytes)> _items = new();
    private readonly Dictionary<PaneId, PaneOutput> _outputs = [];
    private readonly LinkedList<(long First, long Last)> _notificationLosses = [];
    private TaskCompletionSource _changed = NewSignal();
    private long _dropped;
    private long _lastDelivered;
    private long _lastWritten;
    private ExceptionDispatchInfo? _completionError;
    private bool _completed;
    private bool _reading;

    internal ControlModeEventBuffer(
        int capacity,
        Action<int>? afterDequeue = null,
        Action<PaneId>? outputDiscarded = null,
        int maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _capacity = capacity;
        _maxBytes = maxBytes;
        _afterDequeue = afterDequeue;
        _outputDiscarded = outputDiscarded;
    }

    internal bool TryWrite(TmuxEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        long bytes = PayloadBytes(item);
        TaskCompletionSource? changed = null;
        lock (_gate)
        {
            if (_completed)
            {
                return false;
            }

            bool wasEmpty = _items.Count == 0;
            long sequence = ++_lastWritten;
            bool retain = true;
            if (bytes > _maxBytes)
            {
                _dropped++;
                if (item is TmuxOutputEvent output)
                {
                    _outputDiscarded?.Invoke(output.PaneId);
                }
                else
                {
                    NoteNotificationDrop(sequence);
                }

                if (item is TmuxExitEvent)
                {
                    item = new TmuxExitEvent(null);
                    bytes = 0;
                    sequence = ++_lastWritten;
                }
                else
                {
                    retain = false;
                }
            }

            if (retain)
            {
                while (_items.Count > 0
                    && (_items.Count == _capacity || bytes > _maxBytes - _bufferedBytes))
                {
                    Discard(bytePressure: bytes > _maxBytes - _bufferedBytes);
                }

                LinkedListNode<(long Sequence, TmuxEvent Item, long Bytes)> node =
                    _items.AddLast((sequence, item, bytes));
                _bufferedBytes += bytes;
                if (item is TmuxOutputEvent output)
                {
                    if (!_outputs.TryGetValue(output.PaneId, out PaneOutput? pane))
                    {
                        pane = new PaneOutput();
                        _outputs.Add(output.PaneId, pane);
                    }

                    pane.Nodes.Enqueue(node);
                    pane.Bytes += bytes;
                }
            }

            if (wasEmpty)
            {
                changed = _changed;
                _changed = NewSignal();
            }
        }

        changed?.TrySetResult();
        return true;
    }

    // Called under the gate when either admission limit is reached.
    private void Discard(bool bytePressure)
    {
        _dropped++;
        // Under byte pressure, keep small fragments from a quiet pane even if
        // it has more events. Count pressure still selects by event count.
        PaneId? flooding = null;
        long most = -1;
        long oldest = long.MaxValue;
        foreach ((PaneId pane, PaneOutput waiting) in _outputs)
        {
            long measure = bytePressure ? waiting.Bytes : waiting.Nodes.Count;
            long first = waiting.Nodes.Peek().Value.Sequence;
            if (measure > most || (measure == most && first < oldest))
            {
                (flooding, most, oldest) = (pane, measure, first);
            }
        }

        if (flooding is { } loud)
        {
            LinkedListNode<(long Sequence, TmuxEvent Item, long Bytes)> discarded = TakeOutput(loud);
            _bufferedBytes -= discarded.Value.Bytes;
            _items.Remove(discarded);
            _outputDiscarded?.Invoke(loud);
            return;
        }

        LinkedListNode<(long Sequence, TmuxEvent Item, long Bytes)> firstNode = _items.First!;
        _bufferedBytes -= firstNode.Value.Bytes;
        NoteNotificationDrop(firstNode.Value.Sequence);
        _items.RemoveFirst();
    }

    // Called under the gate: a pane's outputs leave in the order they arrived.
    private LinkedListNode<(long Sequence, TmuxEvent Item, long Bytes)> TakeOutput(PaneId pane)
    {
        PaneOutput waiting = _outputs[pane];
        LinkedListNode<(long Sequence, TmuxEvent Item, long Bytes)> oldest = waiting.Nodes.Dequeue();
        waiting.Bytes -= oldest.Value.Bytes;
        if (waiting.Nodes.Count == 0)
        {
            _outputs.Remove(pane);
        }

        return oldest;
    }

    internal long CaptureWatermark()
    {
        lock (_gate)
        {
            return _lastWritten;
        }
    }

    // Reading removes what it reads, so two readers at once would each see
    // only part of the stream; refuse the second rather than split it.
    internal Reader CreateReader(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_reading)
            {
                throw new InvalidOperationException(
                    "This control client's events are already being read. A client has one event "
                    + "stream, and a second reader would see only part of it; open another control "
                    + "client to read independently.");
            }

            _reading = true;
        }

        return new(this, cancellationToken);
    }

    internal void Complete(Exception? error = null)
    {
        TaskCompletionSource? changed = null;
        lock (_gate)
        {
            if (!_completed)
            {
                _completed = true;
                _completionError = error is null
                    ? null
                    : ExceptionDispatchInfo.Capture(error);
                changed = _changed;
            }
        }

        changed?.TrySetResult();
    }

    internal async IAsyncEnumerable<TmuxEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using Reader reader = CreateReader(cancellationToken);
        while (await reader.MoveNextAsync().ConfigureAwait(false) == ControlModeEventRead.Item)
        {
            yield return reader.Current;
        }
    }

    internal sealed class Reader : IAsyncDisposable
    {
        private readonly ControlModeEventBuffer _owner;
        private readonly CancellationToken _cancellationToken;
        private int _disposed;

        internal Reader(ControlModeEventBuffer owner, CancellationToken cancellationToken)
        {
            _owner = owner;
            _cancellationToken = cancellationToken;
        }

        internal TmuxEvent Current { get; private set; } = null!;

        internal ValueTask<ControlModeEventRead> MoveNextAsync() =>
            MoveNextThroughAsync(long.MaxValue);

        internal async ValueTask<ControlModeEventRead> MoveNextThroughAsync(long watermark)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            while (true)
            {
                Task? wait = null;
                ExceptionDispatchInfo? completionError = null;
                ControlModeEventRead result;
                lock (_owner._gate)
                {
                    if (_owner._lastDelivered >= watermark)
                    {
                        return ControlModeEventRead.Boundary;
                    }

                    long beforeNext = _owner._items.First is { } first
                        ? first.Value.Sequence - 1
                        : _owner._lastWritten;
                    long lossThrough = Math.Min(beforeNext, watermark);
                    if (lossThrough > _owner._lastDelivered)
                    {
                        long from = _owner._lastDelivered;
                        long count = lossThrough - from;
                        bool onlyOutput = !_owner.HasNotificationLoss(from, lossThrough);
                        _owner._lastDelivered = lossThrough;
                        _owner.RetireNotificationLosses(lossThrough);
                        Current = new TmuxEventsDroppedEvent(count, _owner._dropped)
                        {
                            OnlyOutput = onlyOutput,
                        };
                        _owner._afterDequeue?.Invoke(_owner._items.Count);
                        return ControlModeEventRead.Item;
                    }

                    if (_owner._items.First is { } head)
                    {
                        (long sequence, TmuxEvent item, long bytes) = head.Value;
                        if (sequence > watermark)
                        {
                            return ControlModeEventRead.Boundary;
                        }

                        _owner._items.RemoveFirst();
                        _owner._bufferedBytes -= bytes;
                        if (item is TmuxOutputEvent output)
                        {
                            _ = _owner.TakeOutput(output.PaneId);
                        }

                        _owner._afterDequeue?.Invoke(_owner._items.Count);
                        Current = item;
                        _owner._lastDelivered = sequence;

                        return ControlModeEventRead.Item;
                    }

                    if (_owner._lastWritten >= watermark)
                    {
                        return ControlModeEventRead.Boundary;
                    }

                    if (_owner._completed)
                    {
                        completionError = _owner._completionError;
                        result = ControlModeEventRead.Completed;
                    }
                    else
                    {
                        wait = _owner._changed.Task;
                        result = default;
                    }
                }

                if (completionError is not null)
                {
                    completionError.Throw();
                }

                if (wait is null)
                {
                    return result;
                }

                await wait.WaitAsync(_cancellationToken).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (_owner._gate)
                {
                    _owner._reading = false;
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    // Notification-drop positions are needed only to prove OnlyOutput. A
    // bounded, sorted set of spans keeps that proof useful under ordinary
    // load; merging old spans may conservatively return false, never true.
    private void NoteNotificationDrop(long sequence)
    {
        LinkedListNode<(long First, long Last)>? previous = _notificationLosses.Last;
        while (previous is not null && previous.Value.First > sequence)
        {
            previous = previous.Previous;
        }

        if (previous is not null && sequence <= previous.Value.Last)
        {
            return;
        }

        LinkedListNode<(long First, long Last)> current;
        if (previous is not null && previous.Value.Last == sequence - 1)
        {
            previous.Value = (previous.Value.First, sequence);
            current = previous;
        }
        else
        {
            current = previous is null
                ? _notificationLosses.AddFirst((sequence, sequence))
                : _notificationLosses.AddAfter(previous, (sequence, sequence));
        }

        if (current.Next is { } next && next.Value.First == current.Value.Last + 1)
        {
            current.Value = (current.Value.First, next.Value.Last);
            _notificationLosses.Remove(next);
        }

        if (_notificationLosses.Count > _capacity)
        {
            LinkedListNode<(long First, long Last)> first = _notificationLosses.First!;
            LinkedListNode<(long First, long Last)> second = first.Next!;
            first.Value = (first.Value.First, second.Value.Last);
            _notificationLosses.Remove(second);
        }
    }

    private bool HasNotificationLoss(long after, long through)
    {
        foreach ((long first, long last) in _notificationLosses)
        {
            if (first > through)
            {
                break;
            }

            if (last > after)
            {
                return true;
            }
        }

        return false;
    }

    private void RetireNotificationLosses(long through)
    {
        while (_notificationLosses.First is { } first && first.Value.Last <= through)
        {
            _notificationLosses.RemoveFirst();
        }
    }

    private sealed class PaneOutput
    {
        internal Queue<LinkedListNode<(long Sequence, TmuxEvent Item, long Bytes)>> Nodes { get; } = new();

        internal long Bytes { get; set; }
    }

    private static long PayloadBytes(TmuxEvent item) => item switch
    {
        TmuxOutputEvent output => Encoding.UTF8.GetByteCount(output.Data),
        TmuxNotificationEvent notification => Encoding.UTF8.GetByteCount(notification.Name)
            + notification.Arguments.Sum(static value => (long)Encoding.UTF8.GetByteCount(value)),
        TmuxExitEvent { Reason: string reason } => Encoding.UTF8.GetByteCount(reason),
        TmuxExitEvent or TmuxPanePausedEvent or TmuxPaneContinuedEvent => 0,
        _ => throw new ArgumentException("The control event has no payload budget definition.", nameof(item)),
    };

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
