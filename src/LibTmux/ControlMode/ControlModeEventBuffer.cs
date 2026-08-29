using System.Runtime.CompilerServices;

namespace LibTmux.Internal;

/// <summary>Buffers notifications without allowing a slow consumer to stall commands.</summary>
internal sealed class ControlModeEventBuffer
{
    private readonly int _capacity;
    private readonly Action? _afterDequeue;
    private readonly object _gate = new();
    private readonly Queue<TmuxEvent> _items = new();
    private TaskCompletionSource _changed = NewSignal();
    private long _dropped;
    private long _reported;
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

            _items.Enqueue(item);
            if (wasEmpty)
            {
                changed = _changed;
                _changed = NewSignal();
            }
        }

        changed?.TrySetResult();
        return true;
    }

    internal void Complete()
    {
        TaskCompletionSource? changed = null;
        lock (_gate)
        {
            if (!_completed)
            {
                _completed = true;
                changed = _changed;
            }
        }

        changed?.TrySetResult();
    }

    internal async IAsyncEnumerable<TmuxEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TmuxEvent? item = null;
            Task? wait = null;
            long dropped = 0;
            long totalDropped = 0;
            bool completed = false;
            lock (_gate)
            {
                if (_items.Count > 0)
                {
                    item = _items.Dequeue();
                    _afterDequeue?.Invoke();
                    totalDropped = _dropped;
                    dropped = totalDropped - _reported;
                    _reported = totalDropped;
                }
                else if (_completed)
                {
                    completed = true;
                }
                else
                {
                    wait = _changed.Task;
                }
            }

            if (completed)
            {
                yield break;
            }

            if (wait is not null)
            {
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (dropped > 0)
            {
                yield return new TmuxEventsDroppedEvent(dropped, totalDropped);
            }

            yield return item!;
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
