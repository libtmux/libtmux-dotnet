using System.Runtime.Versioning;
using BenchmarkDotNet.Attributes;
using LibTmux.Testing;

namespace LibTmux.Benchmarks;

/// <summary>Measures what each execution mode costs against a live tmux.</summary>
/// <remarks>
/// Chaining trades fifty process starts for one; control mode trades them for
/// fifty round trips on an open connection. Which wins is a property of the
/// machine, not the library, so <see cref="ModeBenchmarkConfig"/> measures a
/// distribution at both <see cref="Commands"/> sizes.
/// </remarks>
[UnsupportedOSPlatform("windows")]
[MemoryDiagnoser]
[Config(typeof(ModeBenchmarkConfig))]
public class ModeBenchmarks
{
    private TmuxTestFactory _factory = null!;
    private TemporaryHierarchyScope _scope = null!;
    private Server _server = null!;
    private IControlModeSession _control = null!;

    /// <summary>How many commands each batched measurement runs.</summary>
    [Params(1, 50)]
    public int Commands { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _factory = new TmuxTestFactory();
        TmuxTestOptions options = new(new ServerConnectionOptions(
            tmuxBinaryPath: Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            socketName: $"ltbench-{Guid.NewGuid():N}"[..24],
            configurationFile: "/dev/null"));
        _scope = _factory.CreateHierarchyAsync(options).GetAwaiter().GetResult();
        _server = _scope.Server;
        _control = _server.EnterControlModeAsync().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _control.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>One tmux client started per command.</summary>
    [Benchmark(Baseline = true)]
    public async Task OneShot()
    {
        for (int index = 0; index < Commands; index++)
        {
            await _server.DisplayMessageAsync(
                new DisplayMessageRequest("bench", returnText: true));
        }
    }

    /// <summary>One tmux client started for the whole sequence.</summary>
    [Benchmark]
    public async Task Chained()
    {
        TmuxChain chain = _server.Chain();
        for (int index = 0; index < Commands; index++)
        {
            chain = chain.Then("display-message", "-p", "bench");
        }

        await chain.ExecuteAsync();
    }

    /// <summary>One client already running, so no process cost per command.</summary>
    [Benchmark]
    public async Task ControlMode()
    {
        for (int index = 0; index < Commands; index++)
        {
            await _control.SendAsync(TmuxCommand.Create("display-message", "-p", "bench"));
        }
    }
}
