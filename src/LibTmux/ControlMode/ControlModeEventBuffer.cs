using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace LibTmux.Internal;

internal enum ControlModeEventRead
{
    Item,
    Boundary,
    Completed,
}

/// <summary>Buffers notifications without allowing a slow consumer to stall commands.</summary>
/// <remarks>
/// When full it discards the oldest pane output first, so a flooding pane cannot
/// push out the notifications that describe sessions, windows and layout. Only
/// a buffer holding no output discards its oldest notification.
/// </remarks>
internal sealed class ControlModeEventBuffer
{
    private readonly int _capacity;
    private readonly Action<int>? _afterDequeue;
    private readonly Action<PaneId>? _outputDiscarded;
    private readonly object _gate = new();
    private readonly LinkedList<(long Sequence, TmuxEvent Item)> _items = new();
    private readonly Queue<LinkedListNode<(long Sequence, TmuxEvent Item)>> _outputs = new();
    private TaskCompletionSource _changed = NewSignal();
    private long _dropped;
    private long _reported;
    private bool _notificationDropped;
    private long _lastWritten;
    private ExceptionDispatchInfo? _completionError;
    private bool _completed;

    internal ControlModeEventBuffer(
        int capacity,
        Action<int>? afterDequeue = null,
        Action<PaneId>? outputDiscarded = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _afterDequeue = afterDequeue;
        _outputDiscarded = outputDiscarded;
    }


    internal bool TryWrite(TmuxEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        TaskCompletionSource? changed = null;
        lock (_gate)
        {
            if (_completed)
            {
                return false;
            }

            bool wasEmpty = _items.Count == 0;
            if (_items.Count == _capacity)
            {
                Discard();
            }

            LinkedListNode<(long Sequence, TmuxEvent Item)> node = _items.AddLast((++_lastWritten, item));
            if (item is TmuxOutputEvent)
            {
                _outputs.Enqueue(node);
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

    // Called under the gate with the buffer full.
    private void Discard()
    {
        _dropped++;
        if (_outputs.TryDequeue(out LinkedListNode<(long Sequence, TmuxEvent Item)>? output))
        {
            _items.Remove(output);
            _outputDiscarded?.Invoke(((TmuxOutputEvent)output.Value.Item).PaneId);
            return;
        }

        _items.RemoveFirst();
        _notificationDropped = true;
    }

    internal long CaptureWatermark()
    {
        lock (_gate)
        {
            return _lastWritten;
        }
    }

    internal Reader CreateReader(CancellationToken cancellationToken = default) =>
        new(this, cancellationToken);

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
        private long _lastConsumed;
        private long? _boundaryLossWatermark;

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
            if (_lastConsumed >= watermark || _boundaryLossWatermark == watermark)
            {
                return ControlModeEventRead.Boundary;
            }

            while (true)
            {
                Task? wait = null;
                ExceptionDispatchInfo? completionError = null;
                ControlModeEventRead result;
                lock (_owner._gate)
                {
                    if (_owner._items.First is { } head)
                    {
                        (long sequence, TmuxEvent item) = head.Value;
                        long dropped = _owner._dropped - _owner._reported;
                        if (sequence > watermark)
                        {
                            if (dropped == 0)
                            {
                                return ControlModeEventRead.Boundary;
                            }

                            _boundaryLossWatermark = watermark;
                        }

                        if (dropped > 0)
                        {
                            _owner._reported = _owner._dropped;
                            Current = new TmuxEventsDroppedEvent(dropped, _owner._dropped)
                            {
                                OnlyOutput = !_owner._notificationDropped,
                            };
                            _owner._notificationDropped = false;
                            return ControlModeEventRead.Item;
                        }

                        _owner._items.RemoveFirst();
                        if (_owner._outputs.TryPeek(out LinkedListNode<(long Sequence, TmuxEvent Item)>? output)
                            && ReferenceEquals(output, head))
                        {
                            _owner._outputs.Dequeue();
                        }

                        _owner._afterDequeue?.Invoke(_owner._items.Count);
                        Current = item;
                        _lastConsumed = sequence;

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
