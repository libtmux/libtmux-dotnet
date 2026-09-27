using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using LibTmux.Internal;
using LibTmux.Query;
using Microsoft.FSharp.Core;
using Perfolizer.Horology;

namespace LibTmux.Benchmarks;

/// <summary>Measures portable filter construction and local application over captured panes.</summary>
[UnsupportedOSPlatform("windows")]
[MemoryDiagnoser]
[Config(typeof(FSharpQueryBenchmarkConfig))]
public class FSharpQueryBenchmarks
{
    private Pane[] _panes = null!;
    private QueryDocument _document = null!;
    private LibTmux.FSharp.Filter<Pane> _filter = null!;
    private Func<Pane, bool> _corePredicate = null!;
    private FSharpFunc<Pane, bool> _fsharpPredicate = null!;

    [GlobalSetup]
    public void Setup()
    {
        var generation = new ServerGeneration(1, 1);
        var connection = new TmuxConnection(
            new ServerConnectionOptions { SocketName = "fsharp-query-benchmark" },
            static (_, _) => throw new UnreachableException("A captured query reached tmux."));
        var server = new Server(connection, generation, "tmux 3.7");
        _panes = Enumerable.Range(0, 128)
            .Select(index => new Pane(
                server,
                connection,
                generation,
                new PaneId(index),
                new Dictionary<string, string?>
                {
                    ["pane_current_command"] = index % 4 == 0 ? "nvim" : "sh",
                }))
            .ToArray();

        _document = QueryExtensions.Translate<Pane>(pane => pane.CurrentCommand == "nvim");
        _filter = LibTmux.FSharp.Filter.eq("nvim", LibTmux.FSharp.PaneFields.currentCommand);
        if (!_document.Equals(LibTmux.FSharp.Filter.toDocument(_filter)))
        {
            throw new InvalidOperationException("C# and F# filters produced different documents.");
        }

        _corePredicate = _document.Compile<Pane>();
        _fsharpPredicate = LibTmux.FSharp.Filter.toPredicate(_filter);
        if (_panes.Any(pane => _corePredicate(pane) != _fsharpPredicate.Invoke(pane)))
        {
            throw new InvalidOperationException("C# and F# predicates produced different results.");
        }

        IReadOnlyList<Pane> direct = _panes.Matching(_document);
        IReadOnlyList<Pane> wrapped = LibTmux.FSharp.Query.matching(_filter, _panes);
        if (direct.Count != 32 || !direct.Select(pane => pane.Id).SequenceEqual(wrapped.Select(pane => pane.Id)))
        {
            throw new InvalidOperationException("C# and F# matching produced different panes.");
        }
    }

    /// <summary>Builds and translates one C# expression.</summary>
    [Benchmark(Baseline = true)]
    [SuppressMessage("Performance", "CA1822", Justification = "BenchmarkDotNet discovers instance benchmark methods.")]
    public QueryDocument CSharpConstruct() =>
        QueryExtensions.Translate<Pane>(pane => pane.CurrentCommand == "nvim");

    /// <summary>Builds and translates the equivalent typed F# filter.</summary>
    [Benchmark]
    [SuppressMessage("Performance", "CA1822", Justification = "BenchmarkDotNet discovers instance benchmark methods.")]
    public LibTmux.FSharp.Filter<Pane> FSharpConstruct() =>
        LibTmux.FSharp.Filter.eq("nvim", LibTmux.FSharp.PaneFields.currentCommand);

    /// <summary>Compiles the shared query document.</summary>
    [Benchmark]
    public Func<Pane, bool> CoreCompile() => _document.Compile<Pane>();

    /// <summary>Constructs a new F# filter and compiles its predicate.</summary>
    [Benchmark]
    public FSharpFunc<Pane, bool> FSharpConstructAndCompile() =>
        LibTmux.FSharp.Filter.toPredicate(FSharpConstruct());

    /// <summary>Returns a predicate cached by an existing F# filter.</summary>
    [Benchmark]
    public FSharpFunc<Pane, bool> FSharpCachedPredicate() =>
        LibTmux.FSharp.Filter.toPredicate(_filter);

    /// <summary>Compiles the document and materializes matching captured panes.</summary>
    [Benchmark]
    public IReadOnlyList<Pane> CoreMatching() => _panes.Matching(_document);

    /// <summary>Applies the F# filter through the same core materialization path.</summary>
    [Benchmark]
    public IReadOnlyList<Pane> FSharpMatching() => LibTmux.FSharp.Query.matching(_filter, _panes);

    /// <summary>Materializes with a previously compiled core predicate.</summary>
    [Benchmark]
    public Pane[] CoreCachedPredicateMaterialize() => _panes.Where(_corePredicate).ToArray();

    /// <summary>Materializes with a previously compiled F# predicate adapter.</summary>
    [Benchmark]
    public Pane[] FSharpCachedPredicateMaterialize() =>
        _panes.Where(pane => _fsharpPredicate.Invoke(pane)).ToArray();
}

internal sealed class FSharpQueryBenchmarkConfig : ManualConfig
{
    public FSharpQueryBenchmarkConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(5)
            .WithIterationCount(20)
            .WithIterationTime(TimeInterval.FromMilliseconds(200)));
        AddExporter(JsonExporter.Full);
    }
}
