using System.Runtime.Versioning;
using LibTmux.Examples;

namespace LibTmux.ExampleTests;

/// <summary>Holds the isolation every example depends on.</summary>
/// <remarks>
/// A regression here breaks other tmux servers on the machine rather than
/// these tests, so it is asserted directly.
/// </remarks>
[Collection("Examples")]
[UnsupportedOSPlatform("windows")]
public sealed class ExampleNamespaceTests
{
    [Fact]
    public async Task A_namespace_names_its_own_socket_and_finds_it_under_our_root()
    {
        await using ExampleNamespace world = await ExampleNamespace.EnterAsync(
            "IsolationProof",
            TestContext.Current.CancellationToken);

        Assert.StartsWith(ExampleNamespace.SocketPrefix, world.SocketName, StringComparison.Ordinal);
        Assert.Contains("IsolationProof", world.SocketName, StringComparison.Ordinal);

        string? socket = ExampleNamespace.FindSocket(world.SocketName);
        Assert.NotNull(socket);
        Assert.StartsWith(ExampleNamespace.SocketRoot, socket, StringComparison.Ordinal);

        // What every published example opens with.
        Server bare = await Server.ConnectAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(bare.IsMaterialized);
        Assert.NotEmpty(await bare.GetSessionsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_namespace_can_never_be_the_developers_own_server()
    {
        await using ExampleNamespace world = await ExampleNamespace.EnterAsync(
            "NotDefault",
            TestContext.Current.CancellationToken);

        Assert.NotEqual("default", world.SocketName);
    }

    [Fact]
    public async Task A_namespace_puts_back_every_variable_it_moved()
    {
        string?[] before = Read();

        await using (await ExampleNamespace.EnterAsync(
            "RestoresEnvironment",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(
                ExampleNamespace.SocketRoot,
                Environment.GetEnvironmentVariable("TMUX_TMPDIR"));
            Assert.NotNull(Environment.GetEnvironmentVariable("LIBTMUX_SOCKET_NAME"));
        }

        Assert.Equal(before, Read());
    }

    [Fact]
    public async Task A_namespace_leaves_no_socket_and_no_directory_behind()
    {
        string name;
        string directory;
        await using (ExampleNamespace world = await ExampleNamespace.EnterAsync(
            "LeavesNothing",
            TestContext.Current.CancellationToken))
        {
            name = world.SocketName;
            directory = world.Directory;
            Assert.True(Directory.Exists(directory));
        }

        Assert.Null(ExampleNamespace.FindSocket(name));
        Assert.False(Directory.Exists(directory));
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    [Theory(Skip = "Requires Linux process identity observation.", SkipUnless = nameof(IsLinux))]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Setup_failure_cleans_up_and_failed_disposal_remains_retryable(bool failSetup)
    {
        string? priorBinary = Environment.GetEnvironmentVariable("LIBTMUX_TMUX");
        string?[] before = Read();
        string cwd = Environment.CurrentDirectory;
        string root = Path.Combine(ExampleNamespace.SocketRoot, "fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string wrapper = Path.Combine(root, "tmux");
        string failureFlag = Path.Combine(root, "fail");
        string log = Path.Combine(root, "arguments");
        string identity = Path.Combine(root, "daemon-stat");
        string binary = priorBinary ?? "/usr/bin/tmux";
        ExampleNamespace? world = null;
        string? socket = null;
        try
        {
            await File.WriteAllTextAsync(wrapper,
                $"#!/bin/sh\nprintf '%s\\n' \"$@\" >> '{log}'\n"
                + "socket=; previous=; for argument in \"$@\"; do if test \"$previous\" = -S; then socket=\"$argument\"; fi; previous=\"$argument\"; done\n"
                + $"case \"$*\" in *' -s example'*) pid=$(\"{binary}\" -N -S \"$socket\" display-message -p '#{{pid}}'); cat /proc/$pid/stat > '{identity}'; "
                + (failSetup ? "echo 'injected population failure' >&2; exit 1; " : "")
                + ";; esac\n"
                + $"case \"$*\" in *kill-server*) if test -f '{failureFlag}'; then echo 'injected teardown failure' >&2; exit 1; fi;; esac\n"
                + $"exec '{binary}' \"$@\"\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("LIBTMUX_TMUX", wrapper);
            if (failSetup)
            {
                Exception? error = await Record.ExceptionAsync(async () => world = await ExampleNamespace.EnterAsync("SetupFailure", TestContext.Current.CancellationToken));
                Assert.NotNull(error);
                Assert.Contains("injected population failure", error.ToString(), StringComparison.Ordinal);
                Assert.Null(OwnedScope.CleanupFailure(error));
            }
            else
            {
                world = await ExampleNamespace.EnterAsync("RetryCleanup", TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(failureFlag, "fail", TestContext.Current.CancellationToken);
                Exception? error = await Record.ExceptionAsync(() => world.DisposeAsync().AsTask());
                Assert.NotNull(error);
                Assert.True(Directory.Exists(world.Directory));
                File.Delete(failureFlag);
                await world.DisposeAsync();
                await world.DisposeAsync();
                Assert.False(Directory.Exists(world.Directory));
            }
            Assert.True(DaemonHasExited(await File.ReadAllTextAsync(identity, TestContext.Current.CancellationToken)), "The owned example daemon is still alive after cleanup.");
            string[] arguments = await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken);
            socket = arguments[Array.IndexOf(arguments, "-S") + 1];
            Assert.StartsWith(ExampleNamespace.SocketRoot, socket, StringComparison.Ordinal);
            Assert.Null(await Server.Open(new ServerConnectionOptions { SocketPath = socket, TmuxBinaryPath = binary }).InspectAsync(TestContext.Current.CancellationToken));
            Assert.Equal(before, Read());
            Assert.Equal(cwd, Environment.CurrentDirectory);
        }
        finally
        {
            File.Delete(failureFlag);
            if (world is not null)
            {
                await world.DisposeAsync();
            }
            if (socket is null && File.Exists(log))
            {
                string[] arguments = await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken);
                int flag = Array.IndexOf(arguments, "-S");
                if (flag >= 0)
                {
                    socket = arguments[flag + 1];
                }
            }
            if (socket is not null)
            {
                Server endpoint = Server.Open(new ServerConnectionOptions { SocketPath = socket, TmuxBinaryPath = binary });
                if (await endpoint.InspectAsync(TestContext.Current.CancellationToken) is not null)
                {
                    await (await endpoint.AdoptAsync(TestContext.Current.CancellationToken)).DisposeAsync();
                }
                File.Delete(socket);
            }
            Environment.SetEnvironmentVariable("LIBTMUX_TMUX", priorBinary);
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool DaemonHasExited(string captured)
    {
        string pid = captured[..captured.IndexOf(' ', StringComparison.Ordinal)];
        string[] fields = captured[(captured.LastIndexOf(')') + 2)..].Split(' ');
        try
        {
            string current = File.ReadAllText($"/proc/{pid}/stat");
            string[] observed = current[(current.LastIndexOf(')') + 2)..].Split(' ');
            return observed[0] == "Z" || fields[19] != observed[19];
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static readonly string[] Moved =
    [
        "TMUX_TMPDIR",
        "TMPDIR",
        "LIBTMUX_SOCKET_NAME",
        "LIBTMUX_SOCKET_PATH",
        "LIBTMUX_MCP_COMMAND",
        "TMUX",
        "TMUX_PANE",
    ];

    private static string?[] Read() => [.. Moved.Select(Environment.GetEnvironmentVariable)];
}
