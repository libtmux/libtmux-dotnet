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
internal sealed class ControlModeEventBuffer
{
    internal const int DefaultMaxBytes = 4 * 1024 * 1024;

    private readonly int _capacity;
    private readonly int _maxBytes;
    private long _bufferedBytes;
    private readonly Action? _afterDequeue;
    private readonly object _gate = new();
    private readonly Queue<(long Sequence, TmuxEvent Item, long Bytes)> _items = new();
    private TaskCompletionSource _changed = NewSignal();
    private long _dropped;
    private long _reported;
    private long _lastWritten;
    private ExceptionDispatchInfo? _completionError;
    private bool _completed;

    internal ControlModeEventBuffer(
        int capacity, Action? afterDequeue = null, int maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _capacity = capacity;
        _maxBytes = maxBytes;
        _afterDequeue = afterDequeue;
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
                if (item is TmuxExitEvent)
                {
                    item = new TmuxExitEvent(null);
                    bytes = 0;
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
                    _bufferedBytes -= _items.Dequeue().Bytes;
                    _dropped++;
                }

                _items.Enqueue((sequence, item, bytes));
                _bufferedBytes += bytes;
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
                    if (_owner._items.Count > 0)
                    {
                        (long sequence, TmuxEvent item, _) = _owner._items.Peek();
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
                            Current = new TmuxEventsDroppedEvent(dropped, _owner._dropped);
                            return ControlModeEventRead.Item;
                        }

                        _owner._bufferedBytes -= _owner._items.Dequeue().Bytes;
                        _owner._afterDequeue?.Invoke();
                        Current = item;
                        _lastConsumed = sequence;

                        return ControlModeEventRead.Item;
                    }

                    long pendingLoss = _owner._dropped - _owner._reported;
                    if (pendingLoss > 0)
                    {
                        _owner._reported = _owner._dropped;
                        Current = new TmuxEventsDroppedEvent(pendingLoss, _owner._dropped);
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

    private static long PayloadBytes(TmuxEvent item) => item switch
    {
        TmuxOutputEvent output => Encoding.UTF8.GetByteCount(output.Data),
        TmuxNotificationEvent notification => Encoding.UTF8.GetByteCount(notification.Name)
            + notification.Arguments.Sum(static value => (long)Encoding.UTF8.GetByteCount(value)),
        TmuxExitEvent { Reason: string reason } => Encoding.UTF8.GetByteCount(reason),
        TmuxExitEvent => 0,
        _ => throw new ArgumentException("The control event has no payload budget definition.", nameof(item)),
    };

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
