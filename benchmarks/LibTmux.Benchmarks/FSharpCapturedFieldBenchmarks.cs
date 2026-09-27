using System.Diagnostics;
using System.Runtime.Versioning;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using LibTmux.Internal;
using Microsoft.FSharp.Core;

namespace LibTmux.Benchmarks;

/// <summary>Measures a captured pane field read with and without F# option conversion.</summary>
[UnsupportedOSPlatform("windows")]
[MemoryDiagnoser]
[Config(typeof(FSharpCapturedFieldBenchmarkConfig))]
public class FSharpCapturedFieldBenchmarks
{
    private Pane _pane = null!;

    /// <summary>Whether the captured field contains a path.</summary>
    [Params(true, false)]
    public bool HasPath { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var generation = new ServerGeneration(1, 1);
        var connection = new TmuxConnection(
            new ServerConnectionOptions { SocketName = "fsharp-field-benchmark" },
            static (_, _) => throw new UnreachableException("A captured field reached tmux."));
        var server = new Server(connection, generation, "tmux 3.7");
        _pane = new Pane(
            server,
            connection,
            generation,
            new PaneId(1),
            new Dictionary<string, string?>
            {
                ["pane_current_path"] = HasPath ? "/benchmark" : null,
            });

        string? direct = _pane.CurrentPath;
        FSharpOption<string>? wrapped = LibTmux.FSharp.Pane.currentPath(_pane);
        if ((direct is not null) != HasPath ||
            (wrapped is not null) != HasPath ||
            !StringComparer.Ordinal.Equals(direct, wrapped?.Value))
        {
            throw new InvalidOperationException("The captured path differs between C# and F#.");
        }
    }

    /// <summary>Returns the core captured field.</summary>
    [Benchmark(Baseline = true)]
    public string? CSharp() => _pane.CurrentPath;

    /// <summary>Returns the F# option for the same captured field.</summary>
    [Benchmark]
    public FSharpOption<string>? FSharp() => LibTmux.FSharp.Pane.currentPath(_pane);
}

internal sealed class FSharpCapturedFieldBenchmarkConfig : ManualConfig
{
    public FSharpCapturedFieldBenchmarkConfig()
    {
        AddJob(Job.Default.WithWarmupCount(10).WithIterationCount(50));
        AddExporter(JsonExporter.Full);
    }
}
