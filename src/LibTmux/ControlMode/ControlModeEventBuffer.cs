using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;

namespace LibTmux.Internal;

/// <summary>Buffers notifications without allowing a slow consumer to stall commands.</summary>
internal sealed class ControlModeEventBuffer
{
    internal const int DefaultMaxBytes = 4 * 1024 * 1024;

    private readonly int _capacity;
    private readonly int _maxBytes;
    private long _bufferedBytes;
    private readonly Action? _afterDequeue;
    private readonly object _gate = new();
    private readonly Queue<(TmuxEvent Event, long Bytes)> _items = new();
    private TaskCompletionSource _changed = NewSignal();
    private long _dropped;
    private long _reported;
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
            if (bytes > _maxBytes)
            {
                _dropped++;
                if (item is TmuxExitEvent)
                {
                    item = new TmuxExitEvent(null);
                    bytes = 0;
                }
            }

            if (bytes <= _maxBytes)
            {
                while (_items.Count > 0
                    && (_items.Count == _capacity || bytes > _maxBytes - _bufferedBytes))
                {
                    _bufferedBytes -= _items.Dequeue().Bytes;
                    _dropped++;
                }

                _items.Enqueue((item, bytes));
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
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TmuxEvent? item = null;
            Task? wait = null;
            long dropped = 0;
            long totalDropped = 0;
            ExceptionDispatchInfo? completionError = null;
            bool completed = false;
            lock (_gate)
            {
                if (_items.Count > 0)
                {
                    (item, long bytes) = _items.Dequeue();
                    _bufferedBytes -= bytes;
                    _afterDequeue?.Invoke();
                    totalDropped = _dropped;
                    dropped = totalDropped - _reported;
                    _reported = totalDropped;
                }
                else if (_dropped != _reported)
                {
                    totalDropped = _dropped;
                    dropped = totalDropped - _reported;
                    _reported = totalDropped;
                }
                else if (_completed)
                {
                    completed = true;
                    completionError = _completionError;
                }
                else
                {
                    wait = _changed.Task;
                }
            }

            if (completed)
            {
                completionError?.Throw();
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

            if (item is not null)
            {
                yield return item;
            }
        }
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
