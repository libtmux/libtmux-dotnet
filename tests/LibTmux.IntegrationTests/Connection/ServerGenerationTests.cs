using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Parity;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Connection;

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollectionDefinition
{
}

[Collection("Process environment")]
[UnsupportedOSPlatform("windows")]
public sealed class ServerGenerationTests
{
    public static TheoryData<TmuxColorMode> SupportedColorModes =>
        [
            TmuxColorMode.Default,
            TmuxColorMode.Colors256,
            TmuxColorMode.TrueColor,
        ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(SupportedColorModes))]
    public async Task Every_supported_color_mode_connects_to_real_tmux(TmuxColorMode colorMode)
    {
        await using RawTmuxTestContext context = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);

        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: context.TmuxBinaryPath,
                socketPath: context.SocketPath,
                configurationFile: "/dev/null",
                colorMode: colorMode),
            TestContext.Current.CancellationToken);
        TmuxCommandResult result = await server.ExecuteCommandAsync(
            ["display-message", "-p", "#{session_id}"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["$0"], result.StandardOutputLines);
    }

    [UnixFact]
    public async Task Stale_entity_cannot_target_a_reused_id()
    {
        await using RawTmuxTestContext context = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        var options = new ServerConnectionOptions(
            tmuxBinaryPath: context.TmuxBinaryPath,
            socketPath: context.SocketPath,
            configurationFile: "/dev/null");
        Server firstServer = await Server.ConnectAsync(
            options,
            TestContext.Current.CancellationToken);
        Session staleSession = await firstServer.GetSessionAsync(
            new SessionId(0),
            TestContext.Current.CancellationToken);
        ServerGeneration expected = staleSession.Generation;

        RawTmuxResult stopped = await context.ExecuteAsync(
            ["kill-server"],
            TestContext.Current.CancellationToken);
        Assert.Equal(0, stopped.ExitCode);

        // kill-server returns before the daemon has finished exiting, so a
        // new-session issued straight after can land on a server that is on its
        // way out and be refused.
        await context.WaitForSettledAsync(TestContext.Current.CancellationToken);
        RawTmuxResult restarted = await context.ExecuteAsync(
            [
                "new-session",
                "-d",
                "-s",
                context.SessionName,
                "-x",
                "80",
                "-y",
                "24",
            ],
            TestContext.Current.CancellationToken);
        Assert.Equal(0, restarted.ExitCode);

        Server successorServer = await Server.ConnectAsync(
            options,
            TestContext.Current.CancellationToken);
        Session successorSession = await successorServer.GetSessionAsync(
            new SessionId(0),
            TestContext.Current.CancellationToken);
        ServerGeneration actual = successorSession.Generation;
        Assert.NotEqual(expected, actual);
        Assert.Equal(firstServer, successorServer);

        // The stale handle can still resolve the identifier, because the
        // replacement reuses it. It must not answer with a handle that names
        // the new server's session while reporting the old server as its owner.
        StaleServerGenerationException lookup =
            await Assert.ThrowsAsync<StaleServerGenerationException>(
                () => firstServer.GetSessionAsync(
                    new SessionId(0),
                    TestContext.Current.CancellationToken));
        Assert.Equal(expected, lookup.Expected);
        Assert.Equal(actual, lookup.Actual);

        StaleServerGenerationException error =
            await Assert.ThrowsAsync<StaleServerGenerationException>(
                () => staleSession.ExecuteCommandAsync(
                    ["kill-session"],
                    cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(expected, error.Expected);
        Assert.Equal(actual, error.Actual);
        RawTmuxResult successorStillExists = await context.ExecuteAsync(
            ["has-session", "-t", successorSession.Id.ToString()],
            TestContext.Current.CancellationToken);
        Assert.Equal(0, successorStillExists.ExitCode);
    }

    [UnixFact]
    public async Task Socket_path_connect_materializes_before_one_initializer_call()
    {
        await using RawTmuxTestContext context = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        int initializerCalls = 0;
        Server? initializedServer = null;
        var options = new ServerConnectionOptions(
            tmuxBinaryPath: context.TmuxBinaryPath,
            socketPath: context.SocketPath,
            configurationFile: "/dev/null",
            childEnvironment: new Dictionary<string, string?>
            {
                ["TMUX"] = "/ignored,1,0",
                ["TMUX_TMPDIR"] = Path.Combine(Path.GetTempPath(), $"ignored-{Guid.NewGuid():N}"),
            },
            initializeAsync: (server, _) =>
            {
                initializerCalls++;
                initializedServer = server;
                Assert.True(server.IsMaterialized);
                Assert.NotNull(server.Generation);
                return ValueTask.CompletedTask;
            });
        Server opened = Server.Open(options);

        Server connected = await opened.ConnectAsync(TestContext.Current.CancellationToken);
        Server connectedAgain = await connected.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.False(opened.IsMaterialized);
        Assert.NotSame(opened, connected);
        Assert.Same(connected, connectedAgain);
        Assert.Same(connected, initializedServer);
        Assert.Equal(1, initializerCalls);
        Assert.Equal(options, connected.ConnectionOptions);
    }

    [UnixFact]
    public async Task Socket_name_connects_to_the_named_endpoint()
    {
        string tmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";
        string socketName = $"ltcs-{Guid.NewGuid():N}";
        var transport = new TmuxProcessTransport(
            tmuxBinaryPath,
            ["-f", "/dev/null", "-L", socketName],
            launcher: startInfo =>
            {
                RawTmuxTestContext.ConfigureEnvironment(startInfo);
                return Process.Start(startInfo)
                    ?? throw new InvalidOperationException("tmux did not start.");
            });
        TmuxCommandResult started = await transport.ExecuteAsync(
            ["new-session", "-d", "-s", socketName, "-x", "80", "-y", "24"],
            TestContext.Current.CancellationToken);
        Assert.Equal(0, started.ExitCode);

        try
        {
            Server server = await Server.ConnectAsync(
                new ServerConnectionOptions(
                    tmuxBinaryPath: tmuxBinaryPath,
                    socketName: socketName,
                    configurationFile: "/dev/null"),
                TestContext.Current.CancellationToken);
            Session session = await server.GetSessionAsync(
                new SessionId(0),
                TestContext.Current.CancellationToken);

            Assert.True(server.IsMaterialized);
            Assert.Equal(new SessionId(0), session.Id);
            Assert.Equal(server.Generation, session.Generation);
        }
        finally
        {
            await transport.ExecuteAsync(
                ["kill-server"],
                TestContext.Current.CancellationToken);
        }
    }

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Named_endpoint_is_frozen_across_ambient_tmux_tmpdir_changes(
        bool useFactory)
    {
        string tmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux";

        // This test nests a root of its own inside the temporary directory and
        // then names a socket within it, so the nonce lands in the path twice.
        // A Unix socket path is capped at 107 bytes, and a full identifier at
        // both depths spends the whole budget before tmux is even reached.
        string nonce = Guid.NewGuid().ToString("N")[..8];
        string socketName = $"ltcs-{nonce}";
        string firstRoot = Path.Combine(Path.GetTempPath(), $"ltcs-root-a-{nonce}");
        string secondRoot = Path.Combine(Path.GetTempPath(), $"ltcs-root-b-{nonce}");
        string firstSession = $"first-{nonce}";
        string secondSession = $"second-{nonce}";
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var firstTransport = CreateNamedTransport(tmuxBinaryPath, firstRoot, socketName);
        var secondTransport = CreateNamedTransport(tmuxBinaryPath, secondRoot, socketName);
        string? originalTmuxTmpdir = Environment.GetEnvironmentVariable("TMUX_TMPDIR");

        try
        {
            Assert.Equal(
                0,
                (await firstTransport.ExecuteAsync(
                    ["new-session", "-d", "-s", firstSession],
                    TestContext.Current.CancellationToken)).ExitCode);
            Assert.Equal(
                0,
                (await secondTransport.ExecuteAsync(
                    ["new-session", "-d", "-s", secondSession],
                    TestContext.Current.CancellationToken)).ExitCode);
            Environment.SetEnvironmentVariable("TMUX_TMPDIR", firstRoot);
            var options = new ServerConnectionOptions(
                tmuxBinaryPath: tmuxBinaryPath,
                socketName: useFactory ? null : socketName,
                socketNameFactory: useFactory ? () => socketName : null,
                configurationFile: "/dev/null");
            Server opened = Server.Open(options);

            Environment.SetEnvironmentVariable("TMUX_TMPDIR", secondRoot);
            Server secondIdentity = Server.Open(options);
            Server connected = await opened.ConnectAsync(TestContext.Current.CancellationToken);
            TmuxCommandResult result = await connected.ExecuteCommandAsync(
                ["display-message", "-p", "#{session_name}"],
                TestContext.Current.CancellationToken);

            Assert.Same(options, connected.ConnectionOptions);
            Assert.NotEqual(opened, secondIdentity);
            Assert.Equal([firstSession], result.StandardOutputLines);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMUX_TMPDIR", originalTmuxTmpdir);
            await firstTransport.ExecuteAsync(["kill-server"], CancellationToken.None);
            await secondTransport.ExecuteAsync(["kill-server"], CancellationToken.None);
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [UnixFact]
    public async Task Implicit_default_endpoint_ignores_explicit_child_tmux()
    {
        await using RawTmuxTestContext decoy = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        string nonce = Guid.NewGuid().ToString("N");
        string socketRoot = Path.Combine(Path.GetTempPath(), $"ltcs-default-{nonce}");
        string expectedSession = $"expected-{nonce}";
        Directory.CreateDirectory(socketRoot);
        var expectedTransport = CreateNamedTransport(decoy.TmuxBinaryPath, socketRoot, "default");

        try
        {
            Assert.Equal(
                0,
                (await expectedTransport.ExecuteAsync(
                    ["new-session", "-d", "-s", expectedSession],
                    TestContext.Current.CancellationToken)).ExitCode);
            RawTmuxResult decoyPid = await decoy.ExecuteAsync(
                ["display-message", "-p", "#{pid}"],
                TestContext.Current.CancellationToken);
            Assert.Equal(0, decoyPid.ExitCode);
            string explicitTmux = $"{decoy.SocketPath},{Assert.Single(decoyPid.StandardOutputLines)},0";

            Server server = await Server.ConnectAsync(
                new ServerConnectionOptions(
                    tmuxBinaryPath: decoy.TmuxBinaryPath,
                    configurationFile: "/dev/null",
                    childEnvironment: new Dictionary<string, string?>
                    {
                        ["TMUX"] = explicitTmux,
                        ["TMUX_TMPDIR"] = socketRoot,
                    }),
                TestContext.Current.CancellationToken);
            TmuxCommandResult result = await server.ExecuteCommandAsync(
                ["display-message", "-p", "#{session_name}"],
                TestContext.Current.CancellationToken);

            Assert.Equal([expectedSession], result.StandardOutputLines);
        }
        finally
        {
            await expectedTransport.ExecuteAsync(["kill-server"], CancellationToken.None);
            Directory.Delete(socketRoot, recursive: true);
        }
    }

    [UnixFact]
    public async Task Typed_lookups_match_exact_ids_and_report_only_successful_absence_as_not_found()
    {
        await using RawTmuxTestContext context = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: context.TmuxBinaryPath,
                socketPath: context.SocketPath,
                configurationFile: "/dev/null"),
            TestContext.Current.CancellationToken);

        Session session = await server.GetSessionAsync(
            new SessionId(0),
            TestContext.Current.CancellationToken);
        Window window = await server.GetWindowAsync(
            new WindowId(0),
            TestContext.Current.CancellationToken);
        Pane pane = await server.GetPaneAsync(
            new PaneId(0),
            TestContext.Current.CancellationToken);

        Assert.Equal(new SessionId(0), session.Id);
        Assert.Equal(new WindowId(0), window.Id);
        Assert.Equal(new PaneId(0), pane.Id);
        Assert.Equal(session.Generation, window.Generation);
        Assert.Equal(window.Generation, pane.Generation);
        TmuxObjectNotFoundException missing =
            await Assert.ThrowsAsync<TmuxObjectNotFoundException>(
                () => server.GetPaneAsync(
                    new PaneId(int.MaxValue),
                    TestContext.Current.CancellationToken));
        Assert.Equal($"%{int.MaxValue}", missing.Target);
    }

    [UnixFact]
    public async Task Lookup_command_failure_is_not_reclassified_as_not_found()
    {
        await using RawTmuxTestContext context = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: context.TmuxBinaryPath,
                socketPath: context.SocketPath,
                configurationFile: "/dev/null"),
            TestContext.Current.CancellationToken);
        RawTmuxResult stopped = await context.ExecuteAsync(
            ["kill-server"],
            TestContext.Current.CancellationToken);
        Assert.Equal(0, stopped.ExitCode);

        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.GetSessionAsync(
                new SessionId(0),
                TestContext.Current.CancellationToken));
    }

    [UnixFact]
    public async Task Dead_daemon_target_command_preserves_the_logical_nonzero_result()
    {
        await using RawTmuxTestContext context = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: context.TmuxBinaryPath,
                socketPath: context.SocketPath,
                configurationFile: "/dev/null"),
            TestContext.Current.CancellationToken);
        Session session = await server.GetSessionAsync(
            new SessionId(0),
            TestContext.Current.CancellationToken);
        RawTmuxResult stopped = await context.ExecuteAsync(
            ["kill-server"],
            TestContext.Current.CancellationToken);
        Assert.Equal(0, stopped.ExitCode);

        TmuxCommandResult result = await session.ExecuteCommandAsync(
            ["display-message", "-p", "#{session_id}"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(
            ["display-message", "-t", "$0", "-p", "#{session_id}"],
            result.Arguments);
        Assert.Empty(result.StandardOutput.ToArray());
        Assert.NotEmpty(result.StandardError.ToArray());
    }

    [UnixFact]
    public async Task Guard_preserves_target_override_and_ordinary_missing_target_results()
    {
        await using RawTmuxTestContext context = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        RawTmuxResult second = await context.ExecuteAsync(
            ["new-session", "-d", "-s", $"{context.SessionName}-second"],
            TestContext.Current.CancellationToken);
        Assert.Equal(0, second.ExitCode);
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: context.TmuxBinaryPath,
                socketPath: context.SocketPath,
                configurationFile: "/dev/null"),
            TestContext.Current.CancellationToken);
        Session first = await server.GetSessionAsync(
            new SessionId(0),
            TestContext.Current.CancellationToken);

        TmuxCommandResult overridden = await first.ExecuteCommandAsync(
            ["display-message", "-p", "#{session_id}"],
            targetOverride: "$1",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["display-message", "-t", "$1", "-p", "#{session_id}"], overridden.Arguments);
        Assert.Equal(["$1"], overridden.StandardOutputLines);
        TmuxCommandResult missing = await first.ExecuteCommandAsync(
            ["kill-session"],
            targetOverride: "$2147483647",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Equal(
            ["kill-session", "-t", "$2147483647"],
            missing.Arguments);
    }

    private static TmuxProcessTransport CreateNamedTransport(
        string tmuxBinaryPath,
        string socketRoot,
        string socketName) =>
        new(
            tmuxBinaryPath,
            ["-f", "/dev/null", "-L", socketName],
            launcher: startInfo =>
            {
                RawTmuxTestContext.ConfigureEnvironment(startInfo);
                startInfo.Environment["TMUX_TMPDIR"] = socketRoot;
                return Process.Start(startInfo)
                    ?? throw new InvalidOperationException("tmux did not start.");
            });
}

public sealed class ConnectionIntegrationPlatformContractTests
{
    [Fact]
    public void Unix_connection_tests_have_runtime_skip_metadata()
    {
        foreach (Type testType in new[] { typeof(ServerGenerationTests), typeof(Component02ParityTests) })
        {
            foreach (MethodInfo method in testType.GetMethods())
            {
                FactAttribute? fact = method.GetCustomAttribute<FactAttribute>();
                if (fact is null)
                {
                    continue;
                }

                Assert.Equal("Requires a Unix process environment.", fact.Skip);
                Assert.Equal(typeof(UnixTestEnvironment), fact.SkipType);
                Assert.Equal(nameof(UnixTestEnvironment.IsUnix), fact.SkipUnless);
            }
        }
    }
}
