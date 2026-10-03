using System.Diagnostics;
using System.Runtime.Versioning;
using BenchmarkDotNet.Attributes;
using LibTmux.FSharp;
using LibTmux.Internal;
using Microsoft.FSharp.Collections;

namespace LibTmux.Benchmarks;

/// <summary>Measures following some panes' output: a hand filter over every event against the pane watch.</summary>
/// <remarks>
/// A synthetic client delivers output spread over eight panes and answers the
/// watch's liveness checks, so the timing is the stream, not tmux. The watch
/// also lists the server's panes to see which watched panes remain, which the
/// filter does not; against a real server that is one tmux round trip.
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(FSharpControlFoldBenchmarkConfig))]
[UnsupportedOSPlatform("windows")]
public class FSharpPaneWatchBenchmarks : IAsyncDisposable
{
    private const int EventCount = 256;
    private const int PaneCount = 8;

    private SyntheticSession _session = null!;
    private FSharpList<Pane> _watched = null!;
    private HashSet<PaneId> _ids = null!;

    /// <summary>How many of the eight panes are watched.</summary>
    [Params(1, 2, 8)]
    public int Panes { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var generation = new ServerGeneration(1, 1);
        var connection = new TmuxConnection(
            new ServerConnectionOptions { SocketName = "fsharp-watch-benchmark" },
            static (_, _) => throw new UnreachableException("A pane watch reached the server handle."));
        var server = new Server(connection, generation, "tmux 3.7");
        Pane[] panes =
        [
            .. Enumerable.Range(0, PaneCount)
                .Select(id => new Pane(server, connection, generation, new PaneId(id), new Dictionary<string, string?>())),
        ];
        TmuxEvent[] events =
        [
            .. Enumerable.Range(0, EventCount)
                .Select(index => (TmuxEvent)new TmuxOutputEvent(new PaneId(index % PaneCount), "x")),
        ];
        _session = new SyntheticSession(events);
        _watched = ListModule.OfSeq(panes.Take(Panes));
        _ids = [.. panes.Take(Panes).Select(pane => pane.Id)];

        int expected = EventCount / PaneCount * Panes;
        if (await FilterEvents().ConfigureAwait(false) != expected
            || await WatchPanes().ConfigureAwait(false) != expected)
        {
            throw new InvalidOperationException("The filter and the watch counted different output.");
        }
    }

    [GlobalCleanup]
    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Reads every event and keeps the watched panes' output by hand.</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> FilterEvents()
    {
        int count = 0;
        await foreach (TmuxEvent item in Control.events(_session).ConfigureAwait(false))
        {
            if (item is TmuxOutputEvent output && _ids.Contains(output.PaneId))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Reads the same events through <c>Control.watchPanes</c>.</summary>
    [Benchmark]
    public async Task<int> WatchPanes()
    {
        int count = 0;
        await foreach (TmuxEvent item in Control.watchPanes(_watched, _session).ConfigureAwait(false))
        {
            if (item is TmuxOutputEvent)
            {
                count++;
            }
        }

        return count;
    }

    private sealed class SyntheticSession(TmuxEvent[] events) : IControlModeSession, IAsyncEnumerable<TmuxEvent>
    {
        public IAsyncEnumerable<TmuxEvent> Events => this;

        public bool IsRunning => true;

        public IAsyncEnumerator<TmuxEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Reader(events);

        // The watch lists the server's panes; every pane is alive.
        public Task<IReadOnlyList<string>> SendAsync(TmuxCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                [.. Enumerable.Range(0, PaneCount).Select(id => new PaneId(id).ToString())]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Reader(TmuxEvent[] events) : IAsyncEnumerator<TmuxEvent>
        {
            private int _index = -1;

            public TmuxEvent Current => events[_index];

            public ValueTask<bool> MoveNextAsync() => new(++_index < events.Length);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
