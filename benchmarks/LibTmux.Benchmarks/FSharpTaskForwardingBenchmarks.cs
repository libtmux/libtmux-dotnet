using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using LibTmux.Internal;

namespace LibTmux.Benchmarks;

/// <summary>Measures a core task call and the F# function that forwards it.</summary>
[UnsupportedOSPlatform("windows")]
[MemoryDiagnoser]
[Config(typeof(FSharpTaskForwardingBenchmarkConfig))]
public class FSharpTaskForwardingBenchmarks : IDisposable
{
    private Pane _pane = null!;
    private CapturePaneRequest _request = null!;
    private CancellationTokenSource _cancellation = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        var generation = new ServerGeneration(1, 1);
        var connection = new TmuxConnection(
            new ServerConnectionOptions { SocketName = "fsharp-task-benchmark" },
            (request, receivedToken) =>
            {
                if (receivedToken != token)
                {
                    throw new UnreachableException("The capture did not forward its token.");
                }

                if (request.LogicalArguments.SequenceEqual(["-V"]))
                {
                    return Task.FromResult(new TmuxCommandResult(
                        request.LogicalArguments,
                        0,
                        new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("tmux 3.7\n")),
                        ReadOnlyMemory<byte>.Empty,
                        ["tmux 3.7"],
                        []));
                }

                if (!request.LogicalArguments.Contains("capture-pane"))
                {
                    throw new UnreachableException("The capture sent an unexpected command.");
                }

                byte[] output = Encoding.UTF8.GetBytes("1:1\nfixture\n");
                return Task.FromResult(new TmuxCommandResult(
                    request.LogicalArguments,
                    0,
                    new ReadOnlyMemory<byte>(output),
                    ReadOnlyMemory<byte>.Empty,
                    ["1:1", "fixture"],
                    []));
            });
        var server = new Server(connection, generation, "tmux 3.7");
        _pane = new Pane(
            server,
            connection,
            generation,
            new PaneId(1),
            new Dictionary<string, string?>());
        _request = new CapturePaneRequest();

        IReadOnlyList<string> direct = await CSharpCapture();
        IReadOnlyList<string> wrapped = await FSharpCapture();
        if (!direct.SequenceEqual(wrapped) || !direct.SequenceEqual(["fixture"]))
        {
            throw new InvalidOperationException("C# and F# capture results differ.");
        }
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Calls the core pane task directly.</summary>
    [Benchmark(Baseline = true)]
    public Task<IReadOnlyList<string>> CSharpCapture() =>
        _pane.CaptureAsync(_request, _cancellation.Token);

    /// <summary>Calls the F# function that returns the core pane task.</summary>
    [Benchmark]
    public Task<IReadOnlyList<string>> FSharpCapture() =>
        LibTmux.FSharp.Pane.capture(_cancellation.Token, _request, _pane);
}

internal sealed class FSharpTaskForwardingBenchmarkConfig : ManualConfig
{
    public FSharpTaskForwardingBenchmarkConfig()
    {
        AddJob(Job.Default.WithWarmupCount(10).WithIterationCount(50));
        AddExporter(JsonExporter.Full);
    }
}
