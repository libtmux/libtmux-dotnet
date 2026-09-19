using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.Versioning;
using BenchmarkDotNet.Attributes;
using LibTmux.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibTmux.Benchmarks;

/// <summary>Measures what dispatch costs before tmux is involved.</summary>
/// <remarks>
/// The mode benchmarks start real tmux, so a process start hides everything
/// the library itself spends. These replace the transport with a completed
/// task to leave only the dispatcher: the span, the measurement and the
/// deadline. Each is claimed to cost nothing when unused, and that claim is
/// what is measured here.
/// </remarks>
[UnsupportedOSPlatform("windows")]
[MemoryDiagnoser]
public class DispatchOverheadBenchmarks
{
    private static readonly string[] Arguments = ["list-sessions", "-F", "#{session_id}"];

    private TmuxCommandDispatcher _plain = null!;
    private TmuxCommandDispatcher _timed = null!;
    private ActivityListener? _spans;
    private MeterListener? _measurements;

    [GlobalSetup]
    public void Setup()
    {
        _plain = Dispatcher(timeout: null);
        _timed = Dispatcher(TimeSpan.FromSeconds(30));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _spans?.Dispose();
        _measurements?.Dispose();
    }

    /// <summary>Dispatch with nothing listening: the shipped default.</summary>
    [Benchmark(Baseline = true)]
    public Task<TmuxCommandResult> Unobserved() =>
        _plain.ExecuteAsync(Arguments, CancellationToken.None);

    /// <summary>Dispatch with a timeout set but never reached.</summary>
    [Benchmark]
    public Task<TmuxCommandResult> WithDeadline() =>
        _timed.ExecuteAsync(Arguments, CancellationToken.None);

    /// <summary>Dispatch while a tracer is subscribed.</summary>
    [Benchmark]
    public Task<TmuxCommandResult> Traced()
    {
        _spans ??= ListenForSpans();
        return _plain.ExecuteAsync(Arguments, CancellationToken.None);
    }

    /// <summary>Dispatch while a meter is subscribed.</summary>
    [Benchmark]
    public Task<TmuxCommandResult> Metered()
    {
        _measurements ??= ListenForMeasurements();
        return _plain.ExecuteAsync(Arguments, CancellationToken.None);
    }

    private static TmuxCommandDispatcher Dispatcher(TimeSpan? timeout) =>
        new(
            static (asked, _) => Task.FromResult(
                new TmuxCommandResult(
                    asked,
                    0,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty,
                    [],
                    [])),
            new TmuxCommandContext(NullLogger.Instance, "bench", timeout));

    private static ActivityListener ListenForSpans()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TmuxDiagnostics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static MeterListener ListenForMeasurements()
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, subscribing) =>
            {
                if (instrument.Meter.Name == TmuxDiagnostics.MeterName)
                {
                    subscribing.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        listener.Start();
        return listener;
    }
}
