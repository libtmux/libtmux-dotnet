using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;
using LibTmux.UnitTests.Connection;

namespace LibTmux.UnitTests.Entities;

[UnsupportedOSPlatform("windows")]
public sealed class PaneRunValidationTests
{
    [Fact]
    public void Run_markers_use_a_full_guid_and_split_only_the_printed_marker()
    {
        PaneRunner.RunToken token = PaneRunner.RunToken.Create();

        Assert.Matches("^[0-9a-f]{32}$", token.Id);
        Assert.Equal($"lt_b_{token.Id}", token.BeginHead + token.MarkerTail);
        Assert.Equal($"lt_e_{token.Id}", token.EndHead + token.MarkerTail);
        Assert.Equal($"lt_r_{token.Id}", token.Channel);
        Assert.Equal($"@lt_s_{token.Id}", token.StatusOption);
    }

    [Fact]
    public void Nul_in_shell_command_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new PaneRunRequest("printf before\0after").Validate());
    }

    [Fact]
    public async Task Canceled_run_does_not_dispatch_a_command()
    {
        int dispatched = 0;
        Pane pane = CreatePane(() => Interlocked.Increment(ref dispatched));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pane.RunAsync(
                new PaneRunRequest("printf 'never executed\\n'")
                {
                    Timeout = TimeSpan.FromSeconds(1),
                }, cancellation.Token));

        Assert.Equal(0, Volatile.Read(ref dispatched));
    }

    [Fact]
    public async Task Oversized_timeout_is_rejected_before_dispatch()
    {
        int dispatched = 0;
        Pane pane = CreatePane(() => Interlocked.Increment(ref dispatched));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            pane.RunAsync(
                new PaneRunRequest("printf never") { Timeout = TimeSpan.MaxValue },
                TestContext.Current.CancellationToken));

        Assert.Equal(0, Volatile.Read(ref dispatched));
    }

    [Fact]
    public async Task Invalid_output_budget_is_rejected_before_dispatch()
    {
        int dispatched = 0;
        Pane pane = CreatePane(() => Interlocked.Increment(ref dispatched));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            pane.RunAsync(
                new PaneRunRequest("printf never") { MaxOutputBytes = 0 },
                TestContext.Current.CancellationToken));

        Assert.Equal(0, Volatile.Read(ref dispatched));
    }

    private static Pane CreatePane(Action onDispatch)
    {
        var connection = new TmuxConnection(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = "/bin/sh",
                SocketPath = "/tmp/libtmux-command-run-test.sock",
            },
            FakeMultiplexer.AnsweringVersion((request, _) =>
            {
                onDispatch();
                byte[] output = Encoding.UTF8.GetBytes("91:901\n");
                return Task.FromResult(new TmuxCommandResult(
                    request.LogicalArguments,
                    0,
                    output,
                    ReadOnlyMemory<byte>.Empty,
                    Utf8BackslashDecoder.ProjectOutputLines(output),
                    []));
            }));
        var generation = new ServerGeneration(91, 901);
        var server = new Server(connection, generation, "tmux 3.7");
        return new Pane(
            server,
            connection,
            generation,
            new PaneId(1),
            new Dictionary<string, string?>
            {
                ["socket_path"] = "/tmp/libtmux-command-run-test.sock",
            });
    }
}
