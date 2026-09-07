using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibTmux.Internal;
using LibTmux.Mcp;
using LibTmux.UnitTests.Connection;
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
    public void Regex_pattern_byte_bound_is_portable()
    {
        string boundary = new('a', 999);
        string oversized = new('\u00e9', 500);

        ReadTools.ValidateWaitPatterns([boundary], null, 128_000);
        ReadTools.ValidateSearchPatternBudget(boundary, 128_000);
        _ = ReadTools.CompilePattern(boundary, ignoreCase: false);
        _ = ReadTools.CompilePattern(boundary, ignoreCase: true);

        McpException wait = Assert.Throws<McpException>(() =>
            ReadTools.ValidateWaitPatterns([oversized], null, 128_000));
        McpException search = Assert.Throws<McpException>(() =>
            ReadTools.ValidateSearchPatternBudget(oversized, 128_000));

        Assert.Contains("999 UTF-8 bytes", wait.Message, StringComparison.Ordinal);
        Assert.Contains("limit is 999", search.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pattern_count_and_total_bytes_are_bounded()
    {
        string[] tooMany = Enumerable.Range(0, 33).Select(index => $"p{index}").ToArray();
        string[] tooLarge = Enumerable.Repeat(new string('x', 999), 17).ToArray();

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
            FakeMultiplexer.AnsweringVersion((request, _) =>
            {
                Interlocked.Increment(ref dispatches);
                return Task.FromResult(new TmuxCommandResult(
                    request.LogicalArguments,
                    0,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty,
                    [],
                    []));
            }));
        var generation = new ServerGeneration(11, 22);
        var server = new Server(connection, generation, "tmux 3.7");
        using var accessor = new TmuxConnectionAccessor(server);
        await using var activity = new PaneActivityHub();
        var policy = new ServerPolicy { MaxBytes = 4_000 };
        var tools = new ReadTools(accessor, policy, activity);
        var writes = new WriteTools(accessor, policy, activity);

        _ = await Assert.ThrowsAsync<McpException>(() => tools.WaitForTextAsync(
            patterns: [new string('x', 1_000)],
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

    [Fact]
    public void A_wait_refuses_more_than_eight_mebibytes_of_matching_work()
    {
        Regex[] patterns = [ReadTools.CompilePattern("not-present", ignoreCase: false)];

        McpException error = Assert.Throws<McpException>(() => ReadTools.Match(
            patterns,
            [new string('x', 8 * 1024 * 1024 + 1)],
            TestContext.Current.CancellationToken));

        Assert.Contains("matching work limit", error.Message, StringComparison.Ordinal);
    }
}
