using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using LibTmux.FSharp;
using Microsoft.FSharp.Core;
using Perfolizer.Horology;

namespace LibTmux.Benchmarks;

/// <summary>Measures direct control event consumption and the F# event fold over the same finite source.</summary>
[MemoryDiagnoser]
[Config(typeof(FSharpControlFoldBenchmarkConfig))]
public class FSharpControlFoldBenchmarks : IDisposable
{
    private const int EventCount = 128;
    private const int StopAfter = 64;

    private SyntheticSession _session = null!;
    private CancellationTokenSource _cancellation = null!;
    private EventFolder _folder = null!;

    /// <summary>Gets the number of consumed events and their name-length checksum.</summary>
    public readonly record struct FoldResult(int Count, int Checksum);

    [GlobalSetup]
    public async Task Setup()
    {
        _cancellation = new CancellationTokenSource();
        TmuxEvent[] events = Enumerable.Range(0, EventCount)
            .Select(index => (TmuxEvent)new TmuxNotificationEvent($"event-{index}", []))
            .ToArray();
        _session = new SyntheticSession(events, _cancellation.Token);
        _folder = new EventFolder();

        FoldResult direct = await CSharpConsume().ConfigureAwait(false);
        FoldResult folded = await FSharpFold().ConfigureAwait(false);
        int expectedChecksum = events.Take(StopAfter)
            .Aggregate(0, (checksum, item) =>
                unchecked(checksum * 31 + ((TmuxNotificationEvent)item).Name.Length));

        if (direct != folded || direct != new FoldResult(StopAfter, expectedChecksum))
        {
            throw new InvalidOperationException("Direct and F# event folds returned different results.");
        }

        if (_session.ReadCalls != StopAfter * 2 || _session.ReaderDisposals != 2)
        {
            throw new InvalidOperationException("The event folds did not stop and dispose at the same boundary.");
        }
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Consumes the core async event sequence directly.</summary>
    [Benchmark(Baseline = true)]
    public async Task<FoldResult> CSharpConsume()
    {
        FoldResult state = default;

        await foreach (TmuxEvent item in _session.Events.WithCancellation(_cancellation.Token))
        {
            StreamStep<FoldResult> step = await _folder.Invoke(state).Invoke(item).ConfigureAwait(false);

            switch (step)
            {
                case StreamStep<FoldResult>.Continue next:
                    state = next.state;
                    break;
                case StreamStep<FoldResult>.Stop last:
                    return last.state;
                default:
                    throw new InvalidOperationException("The event folder returned a different step.");
            }
        }

        return state;
    }

    /// <summary>Consumes the core async event sequence through the F# fold.</summary>
    [Benchmark]
    public Task<FoldResult> FSharpFold() =>
        Control.foldWhile(_cancellation.Token, _folder, default, Control.events(_session));

    private static FoldResult NextState(FoldResult state, TmuxEvent item)
    {
        if (item is not TmuxNotificationEvent notification)
        {
            throw new InvalidOperationException("The synthetic source returned a different event type.");
        }

        return new FoldResult(
            state.Count + 1,
            unchecked(state.Checksum * 31 + notification.Name.Length));
    }

    private sealed class EventFolder : FSharpFunc<FoldResult, FSharpFunc<TmuxEvent, Task<StreamStep<FoldResult>>>>
    {
        public override FSharpFunc<TmuxEvent, Task<StreamStep<FoldResult>>> Invoke(FoldResult state) =>
            new EventFolderForState(state);
    }

    private sealed class EventFolderForState(FoldResult state) : FSharpFunc<TmuxEvent, Task<StreamStep<FoldResult>>>
    {
        public override Task<StreamStep<FoldResult>> Invoke(TmuxEvent item)
        {
            FoldResult next = NextState(state, item);
            StreamStep<FoldResult> step = next.Count == StopAfter
                ? StreamStep<FoldResult>.NewStop(next)
                : StreamStep<FoldResult>.NewContinue(next);
            return Task.FromResult(step);
        }
    }

    private sealed class SyntheticSession(TmuxEvent[] events, CancellationToken expectedToken) :
        IControlModeSession,
        IAsyncEnumerable<TmuxEvent>
    {
        public long ReadCalls { get; private set; }

        public long ReaderDisposals { get; private set; }

        public IAsyncEnumerable<TmuxEvent> Events => this;

        public bool IsRunning => true;

        public IAsyncEnumerator<TmuxEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (cancellationToken != expectedToken)
            {
                throw new InvalidOperationException("The event reader received a different cancellation token.");
            }

            return new Reader(this, events);
        }

        public Task<IReadOnlyList<string>> SendAsync(TmuxCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => throw new InvalidOperationException("The borrowed control client was disposed.");

        private sealed class Reader(SyntheticSession owner, TmuxEvent[] events) : IAsyncEnumerator<TmuxEvent>
        {
            private int _index = -1;

            public TmuxEvent Current => events[_index];

            public ValueTask<bool> MoveNextAsync()
            {
                owner.ReadCalls++;
                _index++;
                return new ValueTask<bool>(_index < events.Length);
            }

            public ValueTask DisposeAsync()
            {
                owner.ReaderDisposals++;
                return ValueTask.CompletedTask;
            }
        }
    }
}

internal sealed class FSharpControlFoldBenchmarkConfig : ManualConfig
{
    public FSharpControlFoldBenchmarkConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(5)
            .WithIterationCount(20)
            .WithIterationTime(TimeInterval.FromMilliseconds(200)));
        AddExporter(JsonExporter.Full);
    }
}
