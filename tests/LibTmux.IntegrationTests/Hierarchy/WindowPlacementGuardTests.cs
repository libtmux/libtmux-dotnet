using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Hierarchy;

[UnsupportedOSPlatform("windows")]
public sealed class WindowPlacementGuardTests
{
    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData("move-window", false)]
    [InlineData("link-window", false)]
    [InlineData("unlink-window", false)]
    [InlineData("move-window", true)]
    [InlineData("link-window", true)]
    public async Task Source_swapped_after_observation_cannot_mutate_another_window(string operation, bool chain)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        await PrepareAsync(raw, token);
        bool swapped = false;
        var transport = new TmuxProcessTransport(
            raw.TmuxBinaryPath,
            ["-f", "/dev/null", "-S", raw.SocketPath],
            launcher: startInfo =>
            {
                RawTmuxTestContext.ConfigureEnvironment(startInfo);
                return Process.Start(startInfo) ?? throw new InvalidOperationException("tmux did not start.");
            },
            beforeStart: async (startInfo, cancellationToken) =>
            {
                if (!swapped && startInfo.ArgumentList.Contains(operation))
                {
                    swapped = true;
                    Assert.Equal(0, (await raw.ExecuteAsync(
                        ["swap-window", "-d", "-s", "$0:5", "-t", "$0:2"], cancellationToken)).ExitCode);
                }
            });
        var options = new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null"
        };
        var connection = new TmuxConnection(options, transport.ExecuteAsync);
        Server server = await new Server(connection, null, null).ConnectAsync(token);
        Window requested = (await server.GetWindowsAsync(token)).Single(window => window.Index == 5);

        Exception? error = await Record.ExceptionAsync(async () =>
        {
            if (chain)
            {
                TmuxCommand command = operation == "move-window"
                    ? new MoveWindowRequest
                    {
                        Destination = "3",
                        NoSelect = true
                    }.ToCommand(requested)
                    : new LinkWindowRequest("$0")
                    {
                        TargetIndex = "3",
                        Detach = true
                    }.ToCommand(requested);
                await server.Chain().Then(command).Then("new-window", "-d", "-t", "$0:7").ExecuteAsync(token);
            }
            else if (operation == "move-window")
            {
                await requested.MoveAsync(new MoveWindowRequest
                {
                    Destination = "3",
                    NoSelect = true
                }, token);
            }
            else if (operation == "link-window")
            {
                await requested.LinkAsync(new LinkWindowRequest("$0")
                {
                    TargetIndex = "3",
                    Detach = true
                }, token);
            }
            else
            {
                await requested.UnlinkAsync(killIfLast: true, cancellationToken: token);
            }
        });

        Assert.True(swapped);
        await AssertSwappedOnlyAsync(raw, token);
        Assert.IsType<TmuxCommandException>(error);
    }

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_control_commands_guard_the_captured_placement(bool link)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        await PrepareAsync(raw, token);
        Server server = await Server.ConnectAsync(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null"
        }, token);
        Window requested = (await server.GetWindowsAsync(token)).Single(window => window.Index == 5);
        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        Assert.Equal(0, (await raw.ExecuteAsync(["swap-window", "-d", "-s", "$0:5", "-t", "$0:2"], token)).ExitCode);
        TmuxCommand command = link
            ? new LinkWindowRequest("$0")
            {
                TargetIndex = "3",
                Detach = true
            }.ToCommand(requested)
            : new MoveWindowRequest
            {
                Destination = "3",
                NoSelect = true
            }.ToCommand(requested);

        Exception? error = await Record.ExceptionAsync(() => control.SendAsync(command, token));

        await AssertSwappedOnlyAsync(raw, token);
        Assert.IsType<ControlModeCommandException>(error);
        Window refreshed = (await server.GetWindowsAsync(token)).Single(window => window.Index == 2);
        TmuxCommand validCommand = link
            ? new LinkWindowRequest("$0")
            {
                TargetIndex = "3",
                Detach = true
            }.ToCommand(refreshed)
            : new MoveWindowRequest
            {
                Destination = "3",
                NoSelect = true
            }.ToCommand(refreshed);
        await control.SendAsync(validCommand, token);
        string[] expected = link ? ["0:@0", "2:@0", "3:@0", "5:@1"] : ["0:@0", "3:@0", "5:@1"];
        Assert.Equal(expected, (await raw.ExecuteAsync(["list-windows", "-t", "$0", "-F", "#{window_index}:#{window_id}"], token)).StandardOutputLines);
        Assert.Equal(["usable"], await control.SendAsync(TmuxCommand.Create("display-message", "-p", "usable"), token));
    }

    [Fact(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    public async Task A_native_move_failure_after_a_successful_guard_stops_the_chain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        await PrepareAsync(raw, token);
        Server server = await Server.ConnectAsync(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null"
        }, token);
        Window requested = (await server.GetWindowsAsync(token)).Single(window => window.Index == 5);

        await Assert.ThrowsAsync<TmuxCommandException>(() => server.Chain()
            .Then(new MoveWindowRequest
            {
                Destination = "0"
            }.ToCommand(requested))
            .Then("new-window", "-d", "-t", "$0:7")
            .ExecuteAsync(token));

        Assert.Equal(["0:@0", "2:@1", "5:@0"], (await raw.ExecuteAsync(["list-windows", "-t", "$0", "-F", "#{window_index}:#{window_id}"], token)).StandardOutputLines);
    }

    private static async Task PrepareAsync(RawTmuxTestContext raw, CancellationToken token)
    {
        Assert.Equal(0, (await raw.ExecuteAsync(["new-window", "-d", "-t", "$0:2"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["link-window", "-d", "-s", "$0:0", "-t", "$0:5"], token)).ExitCode);
    }

    private static async Task AssertSwappedOnlyAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Assert.Equal(["0:@0", "2:@0", "5:@1"], (await raw.ExecuteAsync(["list-windows", "-t", "$0", "-F", "#{window_index}:#{window_id}"], token)).StandardOutputLines);
}
