using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Connection;

internal static class ConnectionUnixEnvironment
{
    public static bool IsUnix => !OperatingSystem.IsWindows();
}

internal sealed class ConnectionUnixFactAttribute : FactAttribute
{
    public ConnectionUnixFactAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Requires a Unix process environment.";
        SkipType = typeof(ConnectionUnixEnvironment);
        SkipUnless = nameof(ConnectionUnixEnvironment.IsUnix);
    }
}

public sealed class ConnectionValueTests
{

    [Fact]
    public void Typed_ids_validate_and_round_trip_canonical_values()
    {
        Assert.Equal(new SessionId(0), default(SessionId));
        Assert.Equal(new WindowId(0), default(WindowId));
        Assert.Equal(new PaneId(0), default(PaneId));
        Assert.Equal("$12", SessionId.Parse("$12").ToString());
        Assert.Equal("@34", WindowId.Parse("@34").ToString());
        Assert.Equal("%56", PaneId.Parse("%56").ToString());

        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionId(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowId(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaneId(-1));
    }

    [Fact]
    public void Typed_id_try_parse_rejects_every_noncanonical_input_without_throwing()
    {
        string?[] invalid =
        [
            null,
            string.Empty,
            "$",
            "$-1",
            "$+1",
            "$ 1",
            "$1 ",
            "$2147483648",
            "@1",
            "%1",
            "1",
        ];

        foreach (string? text in invalid)
        {
            Assert.False(SessionId.TryParse(text, out SessionId session));
            Assert.Equal(default, session);
        }

        Assert.False(WindowId.TryParse("$1", out WindowId window));
        Assert.Equal(default, window);
        Assert.False(PaneId.TryParse("@1", out PaneId pane));
        Assert.Equal(default, pane);
        Assert.Throws<ArgumentNullException>(() => SessionId.Parse(null!));
        Assert.Throws<FormatException>(() => WindowId.Parse("@2147483648"));
        Assert.Throws<FormatException>(() => PaneId.Parse("%-1"));
    }

    [Fact]
    public void Server_generation_requires_positive_process_and_start_values()
    {
        Assert.Equal(7, new ServerGeneration(7, 11).ProcessId);
        Assert.Equal(11, new ServerGeneration(7, 11).StartTime);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerGeneration(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerGeneration(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerGeneration(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerGeneration(1, -1));
    }

    [Fact]
    public void Connection_options_copy_child_environment_defensively()
    {
        var source = new Dictionary<string, string?>
        {
            ["KEEP"] = "original",
            ["REMOVE"] = null,
        };
        var options = new ServerConnectionOptions(childEnvironment: source);

        source["KEEP"] = "mutated";
        source["ADDED"] = "later";

        Assert.NotNull(options.ChildEnvironment);
        Assert.Equal("original", options.ChildEnvironment["KEEP"]);
        Assert.False(options.ChildEnvironment.ContainsKey("ADDED"));
        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<string, string?>)options.ChildEnvironment)["KEEP"] = "changed");
    }

    [Fact]
    public void Invalid_child_environment_keys_fail_before_command_execution()
    {
        Assert.Throws<ArgumentException>(
            () => new ServerConnectionOptions(
                childEnvironment: new Dictionary<string, string?> { [" "] = "value" }));
        Assert.Throws<ArgumentNullException>(
            () => new ServerConnectionOptions(childEnvironment: new NullKeyEnvironment()));
    }

    [Theory]
    [InlineData("BAD\0KEY")]
    [InlineData("BAD=KEY")]
    public void Process_invalid_child_environment_keys_fail_during_options_construction(
        string key)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new ServerConnectionOptions(
                childEnvironment: new Dictionary<string, string?> { [key] = "value" }));

        Assert.Equal("childEnvironment", error.ParamName);
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("before\0after")]
    public void Child_environment_values_reject_nul_during_options_construction(
        string value)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new ServerConnectionOptions(
                childEnvironment: new Dictionary<string, string?> { ["KEY"] = value }));

        Assert.Equal("childEnvironment", error.ParamName);
    }

    [Fact]
    public void Child_environment_is_validated_and_copied_in_one_enumeration()
    {
        var source = new SingleEnumerationEnvironment();

        var options = new ServerConnectionOptions(childEnvironment: source);

        Assert.Equal(1, source.EnumerationCount);
        Assert.NotNull(options.ChildEnvironment);
        Assert.Equal("value", options.ChildEnvironment["KEY"]);
    }

    [Fact]
    public void Child_environment_removes_inherited_tmux_and_honors_an_explicit_override()
    {
        var startInfo = new ProcessStartInfo("tmux");
        startInfo.Environment["TMUX"] = "inherited";
        startInfo.Environment["PSMUX_SESSION"] = "inherited";
        startInfo.Environment["PSMUX_TARGET_FULL"] = "$9";
        startInfo.Environment["REMOVE"] = "value";
        string? processTmux = Environment.GetEnvironmentVariable("TMUX");
        var overrides = new Dictionary<string, string?>
        {
            ["ADD"] = "child",
            ["REMOVE"] = null,
        };

        TmuxConnection.ApplyChildEnvironment(startInfo, overrides);

        Assert.False(startInfo.Environment.ContainsKey("TMUX"));
        Assert.False(startInfo.Environment.ContainsKey("PSMUX_SESSION"));
        Assert.False(startInfo.Environment.ContainsKey("PSMUX_TARGET_FULL"));
        Assert.False(startInfo.Environment.ContainsKey("REMOVE"));
        Assert.Equal("child", startInfo.Environment["ADD"]);
        Assert.Equal(processTmux, Environment.GetEnvironmentVariable("TMUX"));

        var overriddenStartInfo = new ProcessStartInfo("tmux");
        overriddenStartInfo.Environment["TMUX"] = "inherited";

        TmuxConnection.ApplyChildEnvironment(
            overriddenStartInfo,
            new Dictionary<string, string?>
            {
                ["TMUX"] = "explicit",
                ["PSMUX_SESSION"] = "explicit-session",
            });

        Assert.Equal("explicit", overriddenStartInfo.Environment["TMUX"]);
        Assert.Equal("explicit-session", overriddenStartInfo.Environment["PSMUX_SESSION"]);
        Assert.Equal(processTmux, Environment.GetEnvironmentVariable("TMUX"));

        var emptyStartInfo = new ProcessStartInfo("tmux");
        emptyStartInfo.Environment["TMUX"] = "inherited";
        TmuxConnection.ApplyChildEnvironment(
            emptyStartInfo,
            new Dictionary<string, string?> { ["TMUX"] = string.Empty });
        Assert.Equal(string.Empty, emptyStartInfo.Environment["TMUX"]);

        var removedStartInfo = new ProcessStartInfo("tmux");
        removedStartInfo.Environment["TMUX"] = "inherited";
        TmuxConnection.ApplyChildEnvironment(
            removedStartInfo,
            new Dictionary<string, string?> { ["TMUX"] = null });
        Assert.False(removedStartInfo.Environment.ContainsKey("TMUX"));
    }

    [Fact]
    public void Psmux_child_environment_is_forwarded_through_wslenv_without_routing_state()
    {
        var startInfo = new ProcessStartInfo("psmux.exe");
        startInfo.Environment["WSLENV"] =
            "PATH/p:psmux_session:PSMUX_DATA_DIR/p:TMUX:OTHER/l:tmux_pane:psmux_data_dir/u:psmux_route_debug/u";
        startInfo.Environment["PSMUX_SESSION"] = "inherited";
        startInfo.Environment["Psmux_Route_Debug"] = "1";
        startInfo.Environment["TMUX"] = "inherited";
        startInfo.Environment["tmux_pane"] = "%99";

        TmuxConnection.ApplyChildEnvironment(
            startInfo,
            new Dictionary<string, string?>
            {
                ["PSMUX_DATA_DIR"] = "C:\\isolated\\psmux",
            },
            forwardPsmuxDataDirectoryThroughWsl: true);

        Assert.Equal("C:\\isolated\\psmux", startInfo.Environment["PSMUX_DATA_DIR"]);
        Assert.Equal("PATH/p:OTHER/l:PSMUX_DATA_DIR/w", startInfo.Environment["WSLENV"]);
        Assert.False(startInfo.Environment.ContainsKey("PSMUX_SESSION"));
        Assert.False(startInfo.Environment.ContainsKey("Psmux_Route_Debug"));
        Assert.False(startInfo.Environment.ContainsKey("TMUX"));
        Assert.False(startInfo.Environment.ContainsKey("tmux_pane"));
    }

    [Fact]
    public void Psmux_child_environment_creates_wslenv_when_none_is_inherited()
    {
        var startInfo = new ProcessStartInfo("psmux.exe");
        startInfo.Environment.Remove("WSLENV");

        TmuxConnection.ApplyChildEnvironment(
            startInfo,
            new Dictionary<string, string?>
            {
                ["PSMUX_DATA_DIR"] = "C:\\isolated\\psmux",
            },
            forwardPsmuxDataDirectoryThroughWsl: true);

        Assert.Equal("PSMUX_DATA_DIR/w", startInfo.Environment["WSLENV"]);
    }

    public static TheoryData<TmuxColorMode, string[]> PrefixCases =>
        new()
        {
            { TmuxColorMode.Default, ["-f", "config", "-L", "named"] },
            { TmuxColorMode.Colors256, ["-2", "-f", "config", "-L", "named"] },
            { TmuxColorMode.TrueColor, ["-T", "RGB", "-f", "config", "-L", "named"] },
        };

    [Fact]
    public void Supported_color_modes_keep_their_values_and_value_one_is_reserved()
    {
        Assert.Equal(0, (int)TmuxColorMode.Default);
        Assert.Equal(2, (int)TmuxColorMode.Colors256);
        Assert.Equal(3, (int)TmuxColorMode.TrueColor);
        int factoryCalls = 0;

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new TmuxConnection(
                new ServerConnectionOptions(
                    colorMode: (TmuxColorMode)1,
                    socketNameFactory: () =>
                    {
                        factoryCalls++;
                        return "unused";
                    }),
                FakeMultiplexer.AnsweringVersion(static (request, _) => Task.FromResult(
                    Result(request.LogicalArguments, 0, [], [])))));

        Assert.Equal("colorMode", error.ParamName);
        Assert.Equal(0, factoryCalls);
    }

    [Theory]
    [MemberData(nameof(PrefixCases))]
    public void Prefixes_put_color_before_configuration_before_endpoint(
        TmuxColorMode colorMode,
        string[] expected)
    {
        var options = new ServerConnectionOptions(
            socketName: "named",
            configurationFile: "config",
            colorMode: colorMode);
        var connection = CreateFakeConnection(options);

        Assert.Equal(expected, connection.PrefixArguments);
    }

    [Fact]
    public void No_endpoint_emits_the_explicit_default_socket_name()
    {
        var connection = CreateFakeConnection(new ServerConnectionOptions());

        Assert.Equal(["-L", "default"], connection.PrefixArguments);
    }

    [Fact]
    public void The_environment_names_the_socket_a_connection_left_unqualified()
    {
        var connection = CreateFakeConnection(
            new ServerConnectionOptions(childEnvironment: ChildEnvironment(
                ("LIBTMUX_SOCKET_NAME", "libtmux-example-connect"))));

        Assert.Equal(["-L", "libtmux-example-connect"], connection.PrefixArguments);
    }

    [Fact]
    public void An_environment_socket_path_outranks_an_environment_socket_name()
    {
        string path = Path.Combine(Path.GetTempPath(), "libtmux-env.sock");
        var connection = CreateFakeConnection(
            new ServerConnectionOptions(childEnvironment: ChildEnvironment(
                ("LIBTMUX_SOCKET_PATH", path),
                ("LIBTMUX_SOCKET_NAME", "ignored"))));

        Assert.Equal(["-S", Path.GetFullPath(path)], connection.PrefixArguments);
    }

    [Fact]
    public void An_explicit_socket_name_ignores_the_environment()
    {
        var connection = CreateFakeConnection(new ServerConnectionOptions(
            socketName: "named",
            childEnvironment: ChildEnvironment(("LIBTMUX_SOCKET_NAME", "ignored"))));

        Assert.Equal(["-L", "named"], connection.PrefixArguments);
    }

    [Fact]
    public void A_socket_name_factory_ignores_the_environment()
    {
        var connection = CreateFakeConnection(new ServerConnectionOptions(
            socketNameFactory: static () => "made",
            childEnvironment: ChildEnvironment(("LIBTMUX_SOCKET_NAME", "ignored"))));

        Assert.Equal(["-L", "made"], connection.PrefixArguments);
    }

    [Fact]
    public void An_explicit_socket_name_ignores_an_environment_socket_path()
    {
        var connection = CreateFakeConnection(new ServerConnectionOptions(
            socketName: "named",
            childEnvironment: ChildEnvironment(
                ("LIBTMUX_SOCKET_PATH", "/tmp/libtmux-ignored.sock"))));

        Assert.Equal(["-L", "named"], connection.PrefixArguments);
    }

    [Fact]
    public void This_process_answers_when_the_child_environment_says_nothing()
    {
        const string Name = "libtmux-process-scoped";
        string? before = Environment.GetEnvironmentVariable("LIBTMUX_SOCKET_NAME");
        Environment.SetEnvironmentVariable("LIBTMUX_SOCKET_NAME", Name);
        try
        {
            var connection = CreateFakeConnection(new ServerConnectionOptions());

            Assert.Equal(["-L", Name], connection.PrefixArguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIBTMUX_SOCKET_NAME", before);
        }
    }

    private static Dictionary<string, string?> ChildEnvironment(
        params (string Name, string Value)[] variables)
    {
        Dictionary<string, string?> environment = new(StringComparer.Ordinal);
        foreach ((string name, string value) in variables)
        {
            environment[name] = value;
        }

        return environment;
    }

    [Fact]
    public void Named_endpoint_identity_uses_the_normalized_effective_socket_root()
    {
        string firstRoot = Path.Combine(Path.GetTempPath(), "libtmux-root-one");
        string equivalentRoot = Path.Combine(firstRoot, "nested", "..");
        string secondRoot = Path.Combine(Path.GetTempPath(), "libtmux-root-two");
        Server first = Server.Open(
            new ServerConnectionOptions(
                childEnvironment: new Dictionary<string, string?>
                {
                    ["TMUX_TMPDIR"] = firstRoot,
                }));
        Server equivalent = Server.Open(
            new ServerConnectionOptions(
                socketName: "default",
                childEnvironment: new Dictionary<string, string?>
                {
                    ["TMUX_TMPDIR"] = equivalentRoot,
                }));
        Server distinct = Server.Open(
            new ServerConnectionOptions(
                childEnvironment: new Dictionary<string, string?>
                {
                    ["TMUX_TMPDIR"] = secondRoot,
                }));

        Assert.Equal(first, equivalent);
        Assert.Equal(first.GetHashCode(), equivalent.GetHashCode());
        Assert.NotEqual(first, distinct);
    }

    [Fact]
    public void Empty_or_removed_tmux_tmpdir_uses_the_default_socket_root()
    {
        Server empty = Server.Open(
            new ServerConnectionOptions(
                childEnvironment: new Dictionary<string, string?>
                {
                    ["TMUX_TMPDIR"] = string.Empty,
                }));
        Server removed = Server.Open(
            new ServerConnectionOptions(
                childEnvironment: new Dictionary<string, string?>
                {
                    ["TMUX_TMPDIR"] = null,
                }));
        Server explicitDefault = Server.Open(
            new ServerConnectionOptions(
                socketName: "default",
                childEnvironment: new Dictionary<string, string?>
                {
                    ["TMUX_TMPDIR"] = "/tmp",
                }));

        Assert.Equal(empty, removed);
        Assert.Equal(removed, explicitDefault);
        Assert.Equal(empty.GetHashCode(), explicitDefault.GetHashCode());
    }

    [Fact]
    public void Socket_path_precedes_name_and_factory_without_invoking_superseded_factory()
    {
        int calls = 0;
        string originalPath = Path.Combine("relative", "..", "socket.sock");
        string absolutePath = Path.Combine(Directory.GetCurrentDirectory(), "socket.sock");
        var options = new ServerConnectionOptions(
            socketName: "named",
            socketPath: originalPath,
            socketNameFactory: () =>
            {
                calls++;
                return "factory";
            });
        var connection = CreateFakeConnection(options);
        Server normalized = Server.Open(new ServerConnectionOptions(socketPath: absolutePath));
        Server original = Server.Open(options);

        Assert.Equal(["-S", absolutePath], connection.PrefixArguments);
        Assert.Equal(normalized, original);
        Assert.Equal(originalPath, connection.Options.SocketPath);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Explicit_socket_name_precedes_factory_without_invoking_it()
    {
        int calls = 0;
        var connection = CreateFakeConnection(
            new ServerConnectionOptions(
                socketName: "named",
                socketNameFactory: () =>
                {
                    calls++;
                    return "factory";
                }));

        Assert.Equal(["-L", "named"], connection.PrefixArguments);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Socket_name_factory_resolves_once_per_opened_connection()
    {
        int calls = 0;
        var options = new ServerConnectionOptions(socketNameFactory: () =>
        {
            calls++;
            return "factory";
        });

        Server server = Server.Open(options);
        _ = server.GetHashCode();
        _ = server.ConnectionOptions;
        _ = server.Equals(Server.Open(new ServerConnectionOptions(socketName: "factory")));

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Selected_socket_name_factory_rejects_invalid_results_before_execution(
        string? factoryResult)
    {
        int factoryCalls = 0;
        int executions = 0;
        var options = new ServerConnectionOptions(socketNameFactory: () =>
        {
            factoryCalls++;
            return factoryResult!;
        });

        Assert.Throws<InvalidOperationException>(
            () => new TmuxConnection(
                options,
                FakeMultiplexer.AnsweringVersion((request, _) =>
                {
                    executions++;
                    return Task.FromResult(Result(request.LogicalArguments, 0, [], []));
                })));
        Assert.Equal(1, factoryCalls);
        Assert.Equal(0, executions);
    }

    [Fact]
    public void Server_equality_uses_only_the_normalized_endpoint()
    {
        Server implicitDefault = Server.Open();
        Server explicitDefault = Server.Open(
            new ServerConnectionOptions(socketName: "default", childEnvironment: new Dictionary<string, string?> { ["A"] = "1" }));
        string directPath = Path.Combine(Path.GetTempPath(), "libtmux-equality.sock");
        string normalizedPath = Path.Combine(
            Path.GetTempPath(),
            "unused",
            "..",
            "libtmux-equality.sock");
        Server firstPath = Server.Open(new ServerConnectionOptions(socketPath: directPath));
        Server secondPath = Server.Open(
            new ServerConnectionOptions(
                tmuxBinaryPath: "different-tmux",
                socketPath: normalizedPath,
                colorMode: TmuxColorMode.TrueColor,
                initializeAsync: static (_, _) => ValueTask.CompletedTask,
                childEnvironment: new Dictionary<string, string?>
                {
                    ["TMUX"] = "/ignored,1,0",
                    ["TMUX_TMPDIR"] = "/ignored",
                }));

        Assert.Equal(implicitDefault, explicitDefault);
        Assert.Equal(implicitDefault.GetHashCode(), explicitDefault.GetHashCode());
        Assert.Equal(firstPath, secondPath);
        Assert.NotEqual(firstPath, implicitDefault);
    }

    [Fact]
    public void Open_is_portable_and_leaves_the_server_unmaterialized()
    {
        MethodInfo open = Assert.Single(
            typeof(Server).GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.Name == nameof(Server.Open));

        Server server = Server.Open();

        Assert.Null(open.GetCustomAttribute<UnsupportedOSPlatformAttribute>());
        Assert.False(server.IsMaterialized);
        Assert.Null(server.Generation);
        Assert.Same(ServerConnectionOptions.Default, server.ConnectionOptions);
    }

    private static TmuxConnection CreateFakeConnection(ServerConnectionOptions options) =>
        new(
            options,
            static (request, _) => Task.FromResult(
                Result(request.LogicalArguments, 0, [], [])));

    private static TmuxCommandResult Result(
        IReadOnlyList<string> arguments,
        int exitCode,
        byte[] stdout,
        byte[] stderr) =>
        new(
            arguments,
            exitCode,
            stdout,
            stderr,
            Utf8BackslashDecoder.ProjectOutputLines(stdout),
            Utf8BackslashDecoder.ProjectErrorLines(stderr));

    private sealed class NullKeyEnvironment : IReadOnlyDictionary<string, string?>
    {
        public int Count => 1;

        public IEnumerable<string> Keys => [null!];

        public IEnumerable<string?> Values => ["value"];

        public string? this[string key] => throw new KeyNotFoundException();

        public bool ContainsKey(string key) => false;

        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator()
        {
            yield return new KeyValuePair<string, string?>(null!, "value");
        }

        public bool TryGetValue(string key, out string? value)
        {
            value = null;
            return false;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class SingleEnumerationEnvironment : IReadOnlyDictionary<string, string?>
    {
        public int Count => 1;

        public int EnumerationCount { get; private set; }

        public IEnumerable<string> Keys => ["KEY"];

        public IEnumerable<string?> Values => ["value"];

        public string? this[string key] => key == "KEY"
            ? "value"
            : throw new KeyNotFoundException();

        public bool ContainsKey(string key) => key == "KEY";

        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator()
        {
            EnumerationCount++;
            if (EnumerationCount != 1)
            {
                throw new InvalidOperationException("The environment was enumerated more than once.");
            }

            return new Dictionary<string, string?> { ["KEY"] = "value" }.GetEnumerator();
        }

        public bool TryGetValue(string key, out string? value)
        {
            bool found = key == "KEY";
            value = found ? "value" : null;
            return found;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}

[UnsupportedOSPlatform("windows")]
public sealed class GenerationGuardTests
{
    [ConnectionUnixFact]
    public async Task Guard_uses_one_structural_group_and_hides_guard_output_and_arguments()
    {
        const string Marker = "libtmux_guard_deadbeef";
        var generation = new ServerGeneration(41, 100);
        TmuxCommandRequest? captured = null;
        byte[] groupedOutput = [.. "41:100\n"u8, 0x66, 0x80, 0x0a];
        var connection = new TmuxConnection(
            new ServerConnectionOptions(),
            FakeMultiplexer.AnsweringVersion((request, _) =>
            {
                captured = request;
                return Task.FromResult(Result(request.LogicalArguments, 0, groupedOutput, []));
            }),
            () => Marker);
        TmuxCommandDispatcher dispatcher = connection.CreateEntityDispatcher(generation);
        string[] logical = ["display-message", "-t", "$0", "-p", ";"];

        TmuxCommandResult result = await dispatcher.ExecuteAsync(
            logical,
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(
            [
                "display-message",
                "-p",
                "#{pid}:#{start_time}",
                ";",
                "if-shell",
                "-F",
                "#{==:#{pid}:#{start_time},41:100}",
                string.Empty,
                Marker,
                ";",
                "display-message",
                "-t",
                "$0",
                "-p",
                "\\;",
            ],
            captured.EncodeArguments());
        Assert.Equal(logical, result.Arguments);
        Assert.Equal(new byte[] { 0x66, 0x80, 0x0a }, result.StandardOutput.ToArray());
        Assert.Equal(["f\\x80"], result.StandardOutputLines);
    }

    [ConnectionUnixFact]
    public async Task Same_process_with_new_start_time_throws_exact_generations()
    {
        const string Marker = "libtmux_guard_c0ffee";
        var expected = new ServerGeneration(42, 100);
        var actual = new ServerGeneration(42, 101);
        var connection = new TmuxConnection(
            new ServerConnectionOptions(),
            FakeMultiplexer.AnsweringVersion((request, _) => Task.FromResult(
                Result(
                    request.LogicalArguments,
                    1,
                    "42:101\n"u8.ToArray(),
                    Encoding.UTF8.GetBytes($"unknown command: {Marker}\n")))),
            () => Marker);
        TmuxCommandDispatcher dispatcher = connection.CreateEntityDispatcher(expected);

        StaleServerGenerationException error =
            await Assert.ThrowsAsync<StaleServerGenerationException>(
                () => dispatcher.ExecuteAsync(
                    ["kill-session", "-t", "$0"],
                    TestContext.Current.CancellationToken));

        Assert.Equal(expected, error.Expected);
        Assert.Equal(actual, error.Actual);
    }

    [ConnectionUnixFact]
    public async Task Marker_classification_requires_exit_one_and_one_exact_complete_line()
    {
        const string Marker = "libtmux_guard_badf00d";
        (int ExitCode, string Error)[] ordinaryFailures =
        [
            (2, $"unknown command: {Marker}\n"),
            (1, $"unknown command: {Marker}"),
            (1, $"unknown command: {Marker}\nextra\n"),
            (1, $"unknown command: {Marker}x\n"),
        ];

        foreach ((int exitCode, string stderr) in ordinaryFailures)
        {
            var connection = new TmuxConnection(
                new ServerConnectionOptions(),
                FakeMultiplexer.AnsweringVersion((request, _) => Task.FromResult(
                    Result(
                        request.LogicalArguments,
                        exitCode,
                        "44:201\n"u8.ToArray(),
                        Encoding.UTF8.GetBytes(stderr)))),
                () => Marker);
            TmuxCommandResult result = await connection
                .CreateEntityDispatcher(new ServerGeneration(44, 200))
                .ExecuteAsync(
                    ["kill-window", "-t", "@0"],
                    TestContext.Current.CancellationToken);

            Assert.Equal(exitCode, result.ExitCode);
            Assert.Equal(Encoding.UTF8.GetBytes(stderr), result.StandardError.ToArray());
        }
    }

    [ConnectionUnixFact]
    public async Task Ordinary_nonzero_without_generation_prefix_preserves_the_logical_result()
    {
        byte[] stdout = "client diagnostic"u8.ToArray();
        byte[] stderr = "no server running on /tmp/missing\n"u8.ToArray();
        var connection = new TmuxConnection(
            new ServerConnectionOptions(),
            FakeMultiplexer.AnsweringVersion((request, _) => Task.FromResult(
                Result(request.LogicalArguments, 1, stdout, stderr))),
            () => "libtmux_guard_10203040");
        string[] logical = ["display-message", "-t", "$0", "-p", "#{session_id}"];

        TmuxCommandResult result = await connection
            .CreateEntityDispatcher(new ServerGeneration(45, 202))
            .ExecuteAsync(logical, TestContext.Current.CancellationToken);

        Assert.Equal(logical, result.Arguments);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(stdout, result.StandardOutput.ToArray());
        Assert.Equal(stderr, result.StandardError.ToArray());
        Assert.Equal(["client diagnostic"], result.StandardOutputLines);
        Assert.Equal(["no server running on /tmp/missing"], result.StandardErrorLines);
    }

    [ConnectionUnixFact]
    public async Task Exact_marker_without_generation_prefix_is_not_classified_or_preserved()
    {
        const string Marker = "libtmux_guard_50607080";
        var connection = new TmuxConnection(
            new ServerConnectionOptions(),
            FakeMultiplexer.AnsweringVersion((request, _) => Task.FromResult(
                Result(
                    request.LogicalArguments,
                    1,
                    [],
                    Encoding.UTF8.GetBytes($"unknown command: {Marker}\n")))),
            () => Marker);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => connection
                .CreateEntityDispatcher(new ServerGeneration(46, 203))
                .ExecuteAsync(
                    ["kill-session", "-t", "$0"],
                    TestContext.Current.CancellationToken));
    }

    [ConnectionUnixFact]
    public async Task Transport_exception_arguments_are_remapped_to_the_logical_target_command()
    {
        var root = new IOException("transport root");
        var connection = new TmuxConnection(
            new ServerConnectionOptions(),
            FakeMultiplexer.AnsweringVersion((request, _) => throw new TmuxTransportException(
                "transport failed",
                request.LogicalArguments,
                TmuxDispatchState.NotDispatched,
                root)),
            () => "libtmux_guard_abcd1234");
        string[] logical = ["select-pane", "-t", "%0", "-P", "hostile;value"];

        TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(
            () => connection
                .CreateEntityDispatcher(new ServerGeneration(50, 300))
                .ExecuteAsync(logical, TestContext.Current.CancellationToken));

        Assert.Equal(logical, error.Arguments);
        Assert.Equal(TmuxDispatchState.NotDispatched, error.Dispatch);
        Assert.Same(root, error.InnerException);
        Assert.DoesNotContain(error.Arguments, argument => argument.Contains("guard", StringComparison.Ordinal));
    }

    [ConnectionUnixFact]
    public void Invalid_live_generation_is_rejected_before_marker_or_transport_use()
    {
        int executions = 0;
        int markers = 0;
        var connection = new TmuxConnection(
            new ServerConnectionOptions(),
            FakeMultiplexer.AnsweringVersion((request, _) =>
            {
                executions++;
                return Task.FromResult(Result(request.LogicalArguments, 0, [], []));
            }),
            () =>
            {
                markers++;
                return "libtmux_guard_deadbeef";
            });

        Assert.Throws<ArgumentException>(() => connection.CreateEntityDispatcher(default));
        Assert.Equal(0, executions);
        Assert.Equal(0, markers);
    }

    [ConnectionUnixFact]
    public async Task Discovery_rejects_malformed_or_nonpositive_generation_before_version_lookup()
    {
        foreach (string malformed in new[] { "bad", "0:1", "1:0", "1:-1", "1:2:3" })
        {
            int calls = 0;
            var connection = new TmuxConnection(
                new ServerConnectionOptions(),
                FakeMultiplexer.AnsweringVersion((request, _) =>
                {
                    calls++;
                    return Task.FromResult(
                        Result(
                            request.LogicalArguments,
                            0,
                            Encoding.UTF8.GetBytes($"{malformed}\n"),
                            []));
                }));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => connection.DiscoverAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, calls);
        }
    }

    [ConnectionUnixFact]
    public async Task Entity_equality_binds_typed_id_to_generation()
    {
        var connection = new TmuxConnection(
            new ServerConnectionOptions(),
            FakeMultiplexer.AnsweringVersion(static (request, _) => Task.FromResult(
                Result(request.LogicalArguments, 0, [], []))));
        var generation = new ServerGeneration(60, 400);
        var server = new Server(connection, generation, "tmux 3.7");
        var successor = new Server(connection, new ServerGeneration(61, 401), "tmux 3.7");

        var session = new Session(server, connection, generation, new SessionId(1));
        var equalSession = new Session(server, connection, generation, new SessionId(1));
        var successorSession = new Session(
            successor,
            connection,
            new ServerGeneration(61, 401),
            new SessionId(1));
        var window = new Window(server, connection, generation, new WindowId(2));
        var equalWindow = new Window(server, connection, generation, new WindowId(2));
        var pane = new Pane(server, connection, generation, new PaneId(3));
        var equalPane = new Pane(server, connection, generation, new PaneId(3));

        Assert.Equal(session, equalSession);
        Assert.Equal(session.GetHashCode(), equalSession.GetHashCode());
        Assert.NotEqual(session, successorSession);
        Assert.Equal(window, equalWindow);
        Assert.Equal(pane, equalPane);
        await Task.CompletedTask;
    }

    private static TmuxCommandResult Result(
        IReadOnlyList<string> arguments,
        int exitCode,
        byte[] stdout,
        byte[] stderr) =>
        new(
            arguments,
            exitCode,
            stdout,
            stderr,
            Utf8BackslashDecoder.ProjectOutputLines(stdout),
            Utf8BackslashDecoder.ProjectErrorLines(stderr));
}

public sealed class ConnectionPlatformContractTests
{
    [Fact]
    public void Every_process_backed_server_member_declares_windows_unsupported()
    {
        MethodInfo[] processBacked = typeof(Server)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(method => method.Name is nameof(Server.ConnectAsync)
                or nameof(Server.GetSessionAsync)
                or nameof(Server.GetWindowAsync)
                or nameof(Server.GetPaneAsync))
            .ToArray();

        Assert.Equal(5, processBacked.Length);
        Assert.All(
            processBacked,
            method => Assert.NotNull(
                method.GetCustomAttribute<UnsupportedOSPlatformAttribute>()));
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Requires Windows.", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    [SuppressMessage(
        "Interoperability",
        "CA1416:Validate platform compatibility",
        Justification = "This Windows-only test verifies the production trust gate.")]
    public async Task Process_backed_server_requires_preview_opt_in_on_windows()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-tmux-{Guid.NewGuid():N}.exe");
        Server server = Server.Open(new ServerConnectionOptions(tmuxBinaryPath: missing));

        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => server.ConnectAsync(TestContext.Current.CancellationToken));
    }
}
