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
internal sealed class ControlModeEventBuffer
{
    private readonly int _capacity;
    private readonly Action? _afterDequeue;
    private readonly object _gate = new();
    private readonly Queue<(long Sequence, TmuxEvent Item)> _items = new();
    private TaskCompletionSource _changed = NewSignal();
    private long _dropped;
    private long _reported;
    private long _lastWritten;
    private ExceptionDispatchInfo? _completionError;
    private bool _completed;

    internal ControlModeEventBuffer(int capacity, Action? afterDequeue = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _afterDequeue = afterDequeue;
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
                _items.Dequeue();
                _dropped++;
            }

            _items.Enqueue((++_lastWritten, item));
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
        private (long Sequence, TmuxEvent Item)? _pending;
        private long _lastConsumed;

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
            if (_pending is { } pending)
            {
                Current = pending.Item;
                _pending = null;
                _lastConsumed = pending.Sequence;
                return ControlModeEventRead.Item;
            }

            if (_lastConsumed >= watermark)
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
                        (long sequence, TmuxEvent item) = _owner._items.Peek();
                        long dropped = _owner._dropped - _owner._reported;
                        if (sequence > watermark)
                        {
                            if (dropped > 0)
                            {
                                _owner._reported = _owner._dropped;
                                Current = new TmuxEventsDroppedEvent(dropped, _owner._dropped);
                                return ControlModeEventRead.Item;
                            }

                            return ControlModeEventRead.Boundary;
                        }

                        _owner._items.Dequeue();
                        _owner._afterDequeue?.Invoke();
                        _owner._reported = _owner._dropped;
                        if (dropped > 0)
                        {
                            _pending = (sequence, item);
                            Current = new TmuxEventsDroppedEvent(dropped, _owner._dropped);
                        }
                        else
                        {
                            Current = item;
                            _lastConsumed = sequence;
                        }

                        return ControlModeEventRead.Item;
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
