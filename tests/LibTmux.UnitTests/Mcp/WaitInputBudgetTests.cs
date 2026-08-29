using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibTmux.Internal;
using LibTmux.Mcp;
using ModelContextProtocol;

namespace LibTmux.UnitTests;

[UnsupportedOSPlatform("windows")]
public sealed class WaitInputBudgetTests
{
    [Fact]
    public void Valid_patterns_and_channels_fit_the_minimum_policy()
    {
        ReadTools.ValidateWaitPatterns(
            ["ready\\s+now"],
            ["error|failed"],
            resultMaxBytes: 4_000);
        WriteTools.ValidateChannel("build-ready", resultMaxBytes: 4_000);
    }

    [Fact]
    public void Pattern_count_and_total_bytes_are_bounded()
    {
        string[] tooMany = Enumerable.Range(0, 33).Select(index => $"p{index}").ToArray();
        string[] tooLarge = Enumerable.Repeat(new string('x', 4_096), 5).ToArray();

        McpException count = Assert.Throws<McpException>(() =>
            ReadTools.ValidateWaitPatterns(tooMany, null, 4_000));
        McpException bytes = Assert.Throws<McpException>(() =>
            ReadTools.ValidateWaitPatterns(tooLarge, null, 128_000));

        Assert.Contains("32", count.Message, StringComparison.Ordinal);
        Assert.Contains("16384", bytes.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Escaping_that_cannot_fit_the_result_is_rejected()
    {
        string escaped = new('\n', 700);

        McpException error = Assert.Throws<McpException>(() =>
            ReadTools.ValidateWaitPatterns([escaped], null, 4_000));

        Assert.Contains("result byte ceiling", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_wait_inputs_are_rejected_before_tmux_dispatch()
    {
        int dispatches = 0;
        var connection = new TmuxConnection(
            new ServerConnectionOptions(socketName: "wait-no-dispatch"),
            (request, _) =>
            {
                Interlocked.Increment(ref dispatches);
                return Task.FromResult(new TmuxCommandResult(
                    request.LogicalArguments,
                    0,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty,
                    [],
                    []));
            },
            implementation: TmuxImplementation.Tmux);
        var generation = new ServerGeneration(11, 22);
        var server = new Server(connection, generation, "tmux 3.7");
        using var accessor = new TmuxConnectionAccessor(server);
        await using var activity = new PaneActivityHub();
        var policy = new ServerPolicy { MaxBytes = 4_000 };
        await using var jobs = new JobStore();
        var tools = new ReadTools(accessor, policy, activity);
        var writes = new WriteTools(accessor, policy, activity, jobs);

        _ = await Assert.ThrowsAsync<McpException>(() => tools.WaitForTextAsync(
            patterns: [new string('x', 4_097)],
            cancellationToken: TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<McpException>(() => writes.WaitForChannelAsync(
            new string('x', 4_097),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, dispatches);
    }

    [Fact]
    public void A_cancelled_wait_stops_before_scanning_pane_text()
    {
        Regex[] patterns = [ReadTools.CompilePattern("ready", ignoreCase: false)];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ReadTools.Match(
            patterns,
            Enumerable.Repeat("not yet", 32_768).ToArray(),
            cancellation.Token));
    }
}
