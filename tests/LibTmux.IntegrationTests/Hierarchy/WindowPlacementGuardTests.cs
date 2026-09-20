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
    [InlineData("guarded-unlink", false)]
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
                if (!swapped && startInfo.ArgumentList.Contains(operation == "guarded-unlink" ? "unlink-window" : operation))
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
            else if (operation == "guarded-unlink")
            {
                await requested.UnlinkAsync(true, [new PaneId(0)], token);
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

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData("foreign")]
    [InlineData("replacement")]
    [InlineData("missing")]
    [InlineData("order")]
    [InlineData("matching")]
    [InlineData("linked")]
    public async Task Guarded_unlink_checks_the_exact_pane_membership_at_dispatch(string change)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Assert.Equal(0, (await raw.ExecuteAsync(["split-window", "-d", "-t", "$0:0"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["new-window", "-d", "-t", "$0:1"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["new-window", "-d", "-t", "$0:2"], token)).ExitCode);
        if (change == "linked")
        {
            Assert.Equal(0, (await raw.ExecuteAsync(["link-window", "-d", "-s", "$0:0", "-t", "$0:9"], token)).ExitCode);
        }
        bool dispatched = false;
        var transport = new TmuxProcessTransport(raw.TmuxBinaryPath,
            ["-u", "-f", "/dev/null", "-S", raw.SocketPath],
            launcher: startInfo =>
            {
                RawTmuxTestContext.ConfigureEnvironment(startInfo);
                return Process.Start(startInfo) ?? throw new InvalidOperationException("tmux did not start.");
            },
            beforeStart: async (startInfo, cancellationToken) =>
            {
                if (dispatched || !startInfo.ArgumentList.Contains("unlink-window"))
                {
                    return;
                }
                dispatched = true;
                string[]? mutation = change switch
                {
                    "foreign" or "replacement" => ["join-pane", "-d", "-s", "%2", "-t", "%0"],
                    "missing" => ["kill-pane", "-t", "%1"],
                    "order" => ["swap-pane", "-d", "-s", "%0", "-t", "%1"],
                    _ => null,
                };
                if (mutation is not null)
                {
                    Assert.Equal(0, (await raw.ExecuteAsync(mutation, cancellationToken)).ExitCode);
                }
                if (change == "replacement")
                {
                    Assert.Equal(0, (await raw.ExecuteAsync(["kill-pane", "-t", "%1"], cancellationToken)).ExitCode);
                }
            });
        var connection = new TmuxConnection(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null",
        }, transport.ExecuteAsync);
        Server server = await new Server(connection, null, null).ConnectAsync(token);
        Window window = Assert.Single(await server.GetWindowsAsync(token), item => item.Index == 0);
        PaneId[] expected = [.. (await window.GetPanesAsync(token)).Select(pane => pane.Id)];

        Exception? error = await Record.ExceptionAsync(() => window.UnlinkAsync(true, expected, token));

        Assert.True(dispatched);
        RawTmuxResult panes = await raw.ExecuteAsync(["list-panes", "-a", "-F", "#{pane_id}"], token);
        Assert.Equal(0, panes.ExitCode);
        if (change is "matching" or "linked" or "order")
        {
            Assert.Null(error);
            Assert.DoesNotContain("0:@0", (await raw.ExecuteAsync(
                ["list-windows", "-t", "$0", "-F", "#{window_index}:#{window_id}"], token)).StandardOutputLines);
            Assert.Equal(change == "linked", panes.StandardOutputLines.Contains("%0", StringComparer.Ordinal));
        }
        else
        {
            Assert.Contains("%0", panes.StandardOutputLines);
            if (change is "foreign" or "replacement")
            {
                Assert.Contains("%2", panes.StandardOutputLines);
            }
            Assert.IsType<TmuxCommandException>(error);
        }
    }

    [UnixFact]
    public async Task Guarded_unlink_cannot_destroy_a_replacement_daemons_reused_ids()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await Server.ConnectAsync(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null",
        }, token);
        Window window = Assert.Single(await server.GetWindowsAsync(token));
        PaneId[] expected = [.. (await window.GetPanesAsync(token)).Select(pane => pane.Id)];
        Assert.Equal(0, (await raw.ExecuteAsync(["kill-server"], token)).ExitCode);
        await raw.WaitForSettledAsync(token);
        Assert.Equal(0, (await raw.ExecuteAsync(["new-session", "-d", "-s", "replacement"], token)).ExitCode);

        await Assert.ThrowsAsync<StaleServerGenerationException>(() => window.UnlinkAsync(true, expected, token));

        Assert.Equal(["%0"], (await raw.ExecuteAsync(["list-panes", "-a", "-F", "#{pane_id}"], token)).StandardOutputLines);
    }

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Guarded_split_requires_the_resolved_target_to_remain_in_its_expected_window(
        bool chain, bool moved, bool overrideTarget)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Assert.Equal(0, (await raw.ExecuteAsync(["split-window", "-d", "-t", "%0"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["new-window", "-d", "-t", "$0:1"], token)).ExitCode);
        bool dispatched = false;
        var transport = new TmuxProcessTransport(raw.TmuxBinaryPath,
            ["-u", "-f", "/dev/null", "-S", raw.SocketPath],
            launcher: startInfo =>
            {
                RawTmuxTestContext.ConfigureEnvironment(startInfo);
                return Process.Start(startInfo) ?? throw new InvalidOperationException("tmux did not start.");
            },
            beforeStart: async (startInfo, cancellationToken) =>
            {
                if (!dispatched && startInfo.ArgumentList.Contains("split-window"))
                {
                    dispatched = true;
                    if (moved)
                    {
                        Assert.Equal(0, (await raw.ExecuteAsync(
                            ["join-pane", "-d", "-s", overrideTarget ? "%1" : "%0", "-t", "%2"], cancellationToken)).ExitCode);
                    }
                }
            });
        var connection = new TmuxConnection(new ServerConnectionOptions
        {
            TmuxBinaryPath = raw.TmuxBinaryPath,
            SocketPath = raw.SocketPath,
            ConfigurationFile = "/dev/null",
        }, transport.ExecuteAsync);
        Server server = await new Server(connection, null, null).ConnectAsync(token);
        Pane source = await server.GetPaneAsync(new PaneId(0), token);
        var request = new SplitPaneRequest
        {
            ExpectedWindowId = source.Window.Id,
            Target = overrideTarget ? "%1" : null,
        };
        Pane? created = null;

        Exception? error = await Record.ExceptionAsync(async () =>
        {
            if (chain)
            {
                TmuxCommandResult result = await server.Chain().Then(request.ToCommand(source)).ExecuteAsync(token);
                created = await server.GetPaneAsync(PaneId.Parse(Assert.Single(result.StandardOutputLines)), token);
            }
            else
            {
                created = await source.SplitAsync(request, token);
            }
        });

        Assert.True(dispatched);
        RawTmuxResult panes = await raw.ExecuteAsync(["list-panes", "-a", "-F", "#{pane_id}"], token);
        Assert.Equal(0, panes.ExitCode);
        Assert.Equal(moved ? 3 : 4, panes.StandardOutputLines.Count);
        if (moved)
        {
            Assert.IsType<TmuxCommandException>(error);
        }
        else
        {
            Assert.Null(error);
            Assert.NotNull(created);
            Assert.Equal(source.Window.Id, created.Window.Id);
        }
    }

    private static async Task PrepareAsync(RawTmuxTestContext raw, CancellationToken token)
    {
        Assert.Equal(0, (await raw.ExecuteAsync(["new-window", "-d", "-t", "$0:2"], token)).ExitCode);
        Assert.Equal(0, (await raw.ExecuteAsync(["link-window", "-d", "-s", "$0:0", "-t", "$0:5"], token)).ExitCode);
    }

    private static async Task AssertSwappedOnlyAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Assert.Equal(["0:@0", "2:@0", "5:@1"], (await raw.ExecuteAsync(["list-windows", "-t", "$0", "-F", "#{window_index}:#{window_id}"], token)).StandardOutputLines);
}
