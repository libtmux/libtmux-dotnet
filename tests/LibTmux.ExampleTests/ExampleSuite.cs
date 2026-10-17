using System.Runtime.Versioning;
using LibTmux.Examples;

namespace LibTmux.ExampleTests;

/// <summary>Examples set process-wide variables, so they run one at a time.</summary>
[CollectionDefinition("Examples", DisableParallelization = true)]
public sealed class OneExampleAtATime;

/// <summary>Runs every ordinary tmux example against live tmux, one test each.</summary>
[Collection("Examples")]
[UnsupportedOSPlatform("windows")]
public sealed class ExampleSuite
{
    public static TheoryData<string> Examples =>
    [
        .. ExampleCase.Discover().Select(example => $"{example.Topic}.{example.Id}"),
    ];

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task Example_runs_against_live_tmux(string name)
    {
        ExampleCase example = Find(name);
        TmuxVersion running = await TmuxVersion.DetectAsync(
            Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            TestContext.Current.CancellationToken);
        Assert.SkipWhen(example.WhyNotOn(running) is not null, $"This example {example.WhyNotOn(running)}.");
        await example.RunAsync(TestContext.Current.CancellationToken);
    }

    // A suite that discovered nothing would otherwise pass every test it has.
    [Fact]
    public void At_least_one_example_is_published() => Assert.NotEmpty(ExampleCase.Discover());

    private static ExampleCase Find(string name) =>
        ExampleCase.Discover().Single(
            example => string.Equals($"{example.Topic}.{example.Id}", name, StringComparison.Ordinal));
}
