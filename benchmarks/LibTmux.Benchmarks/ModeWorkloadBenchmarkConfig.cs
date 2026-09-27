using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;

namespace LibTmux.Benchmarks;

internal sealed class ModeWorkloadBenchmarkConfig : ManualConfig
{
    public ModeWorkloadBenchmarkConfig()
    {
        bool smoke = string.Equals(
            Environment.GetEnvironmentVariable("LIBTMUX_BENCH_SMOKE"),
            "1",
            StringComparison.Ordinal);
        AddJob(Job.Default
            .WithStrategy(RunStrategy.Monitoring)
            .WithWarmupCount(smoke ? 0 : 5)
            .WithIterationCount(smoke ? 1 : 20)
            .WithInvocationCount(1)
            .WithUnrollFactor(1));
        AddColumn(StatisticColumn.Median, StatisticColumn.P95, StatisticColumn.Max);
        AddExporter(JsonExporter.Full);
    }
}
