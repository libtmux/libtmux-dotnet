using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Connection;

[UnsupportedOSPlatform("windows")]
public sealed class PsmuxConnectionTests
{
    private const string AuditedBanner =
        "tmux 3.3.8\npsmux 3.3.8 (66cf613 2026-08-18)\n";
    private const string TestBinarySha256 =
        "54e5c54db259218348f966b5d0d0b5153fdef6350074855ea9ce627d20537b0d";
    private static readonly string TestBinaryPath = Path.Combine(
        Path.GetTempPath(),
        "libtmux-audited-psmux.exe");
    private const string TestDataDirectory = "C:\\libtmux-psmux-unit-data";
    private const string TestNamespace = "libtmux_unit_0001";

    [Fact]
    public void Public_options_require_a_pinned_client_and_isolated_endpoint()
    {
        Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                "\\\\server\\share\\psmux.exe",
                TestBinarySha256,
                TestDataDirectory,
                TestNamespace));
        Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                TestBinaryPath,
                "00",
                TestDataDirectory,
                TestNamespace));
        Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                TestBinaryPath,
                new string('a', 64),
                TestDataDirectory,
                TestNamespace));
        Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                TestBinaryPath,
                TestBinarySha256,
                "relative\\data",
                TestNamespace));
        Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                TestBinaryPath,
                TestBinarySha256,
                "/tmp/data",
                TestNamespace));
        Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                TestBinaryPath,
                TestBinarySha256,
                "C:\\",
                TestNamespace));
        Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                TestBinaryPath,
                TestBinarySha256,
                "\\\\server\\share",
                TestNamespace));
        Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                TestBinaryPath,
                TestBinarySha256,
                "\\\\server\\share\\isolated",
                TestNamespace));
        Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                TestBinaryPath,
                TestBinarySha256,
                "C:\\temp\\CON",
                TestNamespace));
        Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                TestBinaryPath,
                TestBinarySha256,
                TestDataDirectory,
                "default"));
        Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                TestBinaryPath,
                TestBinarySha256,
                TestDataDirectory,
                "too-short"));
        Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                TestBinaryPath,
                TestBinarySha256,
                TestDataDirectory,
                "libtmux_Unit_0001"));

        var options = new PsmuxConnectionOptions(
            TestBinaryPath,
            TestBinarySha256.ToUpperInvariant(),
            "c:/libtmux-psmux-unit-data/",
            TestNamespace);
        Assert.Equal(TestBinarySha256, options.ExpectedBinarySha256);
        Assert.Equal(TestBinarySha256, PsmuxServer.SupportedBinarySha256);
        Assert.Equal(TestDataDirectory, options.DataDirectory);
        Assert.Equal(TestNamespace, options.NamespaceName);
    }

    [Fact]
    public void Psmux_endpoint_identity_includes_the_frozen_data_directory()
    {
        Assert.Equal(PsmuxOptions(), PsmuxOptions());
        Server first = Server.Open(PsmuxOptions());
        Server same = Server.Open(PsmuxOptions());
        Server sameCaseVariant = Server.Open(PsmuxOptions(
            dataDirectory: "c:\\LIBTMUX-PSMUX-UNIT-DATA\\"));
        Server other = Server.Open(PsmuxOptions(
            dataDirectory: "C:\\libtmux-psmux-other-data"));

        Assert.Equal(first, same);
        Assert.Equal(first, sameCaseVariant);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public async Task Binary_trust_rejects_missing_build_markers()
    {
        string binary = Path.Combine(
            Path.GetTempPath(),
            $"libtmux-old-psmux-{Guid.NewGuid():N}.exe");
        byte[] contents = Encoding.UTF8.GetBytes("psmux 3.3.7 05cc5d4 2026-07-20");
        await File.WriteAllBytesAsync(binary, contents, TestContext.Current.CancellationToken);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contents));
        try
        {
            NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(
                () => PsmuxBinaryTrust.VerifyAsync(
                    binary,
                    hash,
                    TestContext.Current.CancellationToken));

            Assert.Contains("audited build markers", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(binary);
        }
    }

    [Fact]
    public async Task Binary_trust_streams_hash_and_markers_across_buffer_boundaries()
    {
        string binary = Path.Combine(
            Path.GetTempPath(),
            $"libtmux-streamed-psmux-{Guid.NewGuid():N}.exe");
        byte[] contents = new byte[82032];
        Array.Fill(contents, (byte)'x');
        "66cf613"u8.CopyTo(contents.AsSpan(81917));
        "2026-08-18"u8.CopyTo(contents.AsSpan(82000));
        await File.WriteAllBytesAsync(binary, contents, TestContext.Current.CancellationToken);
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(contents));
        try
        {
            await PsmuxBinaryTrust.VerifyAsync(
                binary,
                hash,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(binary);
        }
    }

    [Fact]
    public async Task Binary_trust_does_not_capture_the_callers_synchronization_context()
    {
        string binary = Path.Combine(
            Path.GetTempPath(),
            $"libtmux-context-psmux-{Guid.NewGuid():N}.exe");
        byte[] contents = "66cf613 2026-08-18"u8.ToArray();
        await File.WriteAllBytesAsync(binary, contents, TestContext.Current.CancellationToken);
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(contents));
        var context = new RecordingSynchronizationContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task verification;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            verification = PsmuxBinaryTrust.VerifyAsync(
                binary,
                hash,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        try
        {
            await verification;
            Assert.Equal(0, context.PostCalls);
        }
        finally
        {
            File.Delete(binary);
        }
    }

    [Fact]
    public async Task Unknown_backend_detects_once_before_rejecting_an_unsafe_argument()
    {
        int calls = 0;
        var connection = new TmuxConnection(
            PsmuxOptions(),
            (request, _) =>
            {
                calls++;
                Assert.Equal(["-V"], request.LogicalArguments);
                return Task.FromResult(Result(request.LogicalArguments, AuditedBanner));
            },
            implementation: TmuxImplementation.Unknown);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => connection.ServerDispatcher.ExecuteAsync(
                ["display-message", "-p", "literal;"],
                TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Two_line_banner_selects_psmux_and_uses_the_sole_session_generation()
    {
        var calls = new List<string[]>();
        var connection = new TmuxConnection(
            PsmuxOptions(),
            (request, _) =>
            {
                string[] arguments = [.. request.LogicalArguments];
                calls.Add(arguments);
                return Task.FromResult(arguments[0] switch
                {
                    "-V" => Result(arguments, AuditedBanner),
                    "list-sessions" => Result(arguments, "41:100\t$7\talpha\n"),
                    "display-message" when IsSelectedGenerationProbe(request) =>
                        Result(arguments, "41:100\t$7\talpha\n"),
                    "display-message" => Result(arguments, "41:100\t$7\talpha\n"),
                    _ => throw new Xunit.Sdk.XunitException("Unexpected command."),
                });
            },
            implementation: TmuxImplementation.Unknown);

        (ServerGeneration generation, string rawVersion) = await connection.DiscoverAsync(
            TestContext.Current.CancellationToken);

        Assert.True(connection.IsPsmux);
        Assert.Equal(new ServerGeneration(41, 100), generation);
        Assert.Equal("tmux 3.3.8", rawVersion);
        Assert.Equal(["-V"], calls[0]);
        Assert.Equal("list-sessions", calls[1][0]);
        Assert.Equal(
            ["display-message", "-p", "-t", "alpha", "#{pid}:#{start_time}\t#{session_id}\t#{session_name}"],
            calls[2]);
        Assert.Equal(
            ["display-message", "-p", "#{pid}:#{start_time}\t#{session_id}\t#{session_name}"],
            calls[3]);
    }

    [Fact]
    public async Task Entity_dispatch_rewrites_namespaced_session_ids_and_preserves_arguments()
    {
        var requests = new List<TmuxCommandRequest>();
        var connection = PsmuxConnection((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(OneSessionResult(request, "literal-value\n"));
        });
        string[] logical = ["display-message", "-p", "-t", "$7:%1", "literal-value"];

        TmuxCommandResult result = await connection
            .CreateEntityDispatcher(new ServerGeneration(41, 100))
            .ExecuteAsync(logical, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["display-message", "-p", "-t", "alpha:.%1", "literal-value"],
            Assert.Single(requests, request => request.LogicalArguments.SequenceEqual(
                ["display-message", "-p", "-t", "alpha:.%1", "literal-value"])).LogicalArguments);
        TmuxCommandRequest dispatched = Assert.Single(
            requests,
            request => request.LogicalArguments.SequenceEqual(
                ["display-message", "-p", "-t", "alpha:.%1", "literal-value"]));
        Assert.Equal("literal-value", dispatched.EncodeArguments()[^1]);
        Assert.Equal(logical, result.Arguments);
    }

    [Fact]
    public async Task Bare_window_and_pane_ids_are_qualified_with_the_visible_session()
    {
        var requests = new List<TmuxCommandRequest>();
        var connection = PsmuxConnection((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(OneSessionResult(request));
        });
        TmuxCommandDispatcher dispatcher = connection.CreateEntityDispatcher(
            new ServerGeneration(41, 100));

        await dispatcher.ExecuteAsync(
            ["display-message", "-p", "-t", "%1", "#{pane_id}"],
            TestContext.Current.CancellationToken);
        await dispatcher.ExecuteAsync(
            ["display-message", "-p", "-t", "@1", "#{window_id}"],
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["display-message", "-p", "-t", "alpha:.%1", "#{pane_id}"],
            Assert.Single(requests, request => request.LogicalArguments.SequenceEqual(
                ["display-message", "-p", "-t", "alpha:.%1", "#{pane_id}"])).LogicalArguments);
        Assert.Equal(
            ["display-message", "-p", "-t", "alpha:@1", "#{window_id}"],
            Assert.Single(requests, request => request.LogicalArguments.SequenceEqual(
                ["display-message", "-p", "-t", "alpha:@1", "#{window_id}"])).LogicalArguments);
    }

    [Fact]
    public async Task All_scope_window_and_pane_queries_are_routed_to_the_exact_session()
    {
        var requests = new List<TmuxCommandRequest>();
        var connection = PsmuxConnection((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(OneSessionResult(request));
        });

        await connection.ServerDispatcher.ExecuteAsync(
            ["list-windows", "-a", "-F", "#{window_id}"],
            TestContext.Current.CancellationToken);
        await connection.ServerDispatcher.ExecuteAsync(
            ["list-panes", "-a", "-F", "#{pane_id}"],
            TestContext.Current.CancellationToken);
        await connection.ServerDispatcher.ExecuteAsync(
            ["list-windows", "-F", "-a"],
            TestContext.Current.CancellationToken);
        await connection.ServerDispatcher.ExecuteAsync(
            ["list-panes", "-F", "-s", "-a"],
            TestContext.Current.CancellationToken);

        Assert.Single(requests, request => request.LogicalArguments.SequenceEqual(
            ["list-windows", "-t", "alpha", "-F", "#{window_id}"]));
        Assert.Single(requests, request => request.LogicalArguments.SequenceEqual(
            ["list-panes", "-t", "alpha", "-s", "-F", "#{pane_id}"]));
        Assert.Single(requests, request => request.LogicalArguments.SequenceEqual(
            ["list-windows", "-t", "alpha", "-F", "-a"]));
        Assert.Single(requests, request => request.LogicalArguments.SequenceEqual(
            ["list-panes", "-t", "alpha", "-s", "-F", "-s"]));
    }

    [Fact]
    public async Task Every_targetable_query_is_bound_to_the_visible_session()
    {
        var requests = new List<TmuxCommandRequest>();
        var connection = PsmuxConnection((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(OneSessionResult(request));
        });

        await connection.ServerDispatcher.ExecuteAsync(
            ["has-session"],
            TestContext.Current.CancellationToken);
        await connection.ServerDispatcher.ExecuteAsync(
            ["list-windows", "-F", "#{window_id}"],
            TestContext.Current.CancellationToken);
        await connection.ServerDispatcher.ExecuteAsync(
            ["list-panes", "-s", "-F", "#{pane_id}"],
            TestContext.Current.CancellationToken);
        await connection.ServerDispatcher.ExecuteAsync(
            ["display-message", "-p", "message"],
            TestContext.Current.CancellationToken);
        await connection.ServerDispatcher.ExecuteAsync(
            ["capture-pane", "-p"],
            TestContext.Current.CancellationToken);

        string[][] expected =
        [
            ["has-session", "-t", "alpha"],
            ["list-windows", "-t", "alpha", "-F", "#{window_id}"],
            ["list-panes", "-t", "alpha", "-s", "-F", "#{pane_id}"],
            ["display-message", "-t", "alpha", "-p", "message"],
            ["capture-pane", "-t", "alpha", "-p"],
        ];
        foreach (string[] command in expected)
        {
            Assert.Single(requests, request => request.LogicalArguments.SequenceEqual(command));
        }
    }

    [Theory]
    [MemberData(nameof(MismatchedSessionTargets))]
    public async Task Mismatched_session_targets_are_rejected_without_query_dispatch(
        string[] command)
    {
        var requests = new List<TmuxCommandRequest>();
        var connection = PsmuxConnection((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(OneSessionResult(request));
        });

        await Assert.ThrowsAsync<NotSupportedException>(
            () => connection.ServerDispatcher.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(requests, request => request.LogicalArguments.SequenceEqual(command));
        Assert.DoesNotContain(requests, request =>
            request.LogicalArguments.Contains("beta", StringComparer.Ordinal)
            || request.LogicalArguments.Contains("$999", StringComparer.Ordinal)
            || request.LogicalArguments.Contains("=$999", StringComparer.Ordinal));
    }

    public static TheoryData<string[]> MismatchedSessionTargets =>
        new()
        {
            { ["has-session", "-t", "$999"] },
            { ["has-session", "-t", "=$999"] },
            { ["has-session", "-t", "beta"] },
            { ["has-session", "-t", "=beta"] },
            { ["display-message", "-p", "-t", "beta:%1", "#{pane_id}"] },
            { ["display-message", "-p", "-t", "=beta:%1", "#{pane_id}"] },
        };

    [Fact]
    public async Task Missing_object_target_fails_before_the_query_can_fall_back_to_active()
    {
        var requests = new List<TmuxCommandRequest>();
        var connection = PsmuxConnection((request, _) =>
        {
            requests.Add(request);
            if (request.LogicalArguments[0] == "list-panes")
            {
                return Task.FromResult(Result(request.LogicalArguments));
            }

            return Task.FromResult(OneSessionResult(request));
        });

        await Assert.ThrowsAsync<TmuxObjectNotFoundException>(
            () => connection.CreateEntityDispatcher(new ServerGeneration(41, 100)).ExecuteAsync(
                ["capture-pane", "-p", "-t", "%99"],
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(requests, request => request.LogicalArguments.SequenceEqual(
            ["capture-pane", "-p", "-t", "alpha:.%99"]));
    }

    [Fact]
    public async Task Ambiguous_target_tokens_are_rejected_before_session_discovery()
    {
        int calls = 0;
        var connection = PsmuxConnection((request, _) =>
        {
            calls++;
            return Task.FromResult(Result(request.LogicalArguments));
        });
        TmuxCommandDispatcher dispatcher = connection.CreateEntityDispatcher(
            new ServerGeneration(41, 100));
        string[][] commands =
        [
            ["send-keys", "-t", "%1", "-t", "$0"],
            ["send-keys", "--", "-t", "$0"],
            ["display-message", "-p", "-t", "child__alpha:%1"],
            ["display-message", "-p", "-t", "alpha:.+"],
            ["capture-pane", "-p", "-t", "alpha:.{last}"],
            ["capture-pane", "-p", "-t", ":.+"],
        ];

        foreach (string[] command in commands)
        {
            await Assert.ThrowsAsync<NotSupportedException>(
                () => dispatcher.ExecuteAsync(
                    command,
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Multiple_sessions_fail_before_entity_dispatch_and_grouped_commands_are_rejected()
    {
        int calls = 0;
        var connection = PsmuxConnection((request, _) =>
        {
            calls++;
            return Task.FromResult(Result(
                request.LogicalArguments,
                "41:100\t$7\talpha\n42:101\t$8\tbeta\n"));
        });
        var generation = new ServerGeneration(41, 100);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => connection.CreateEntityDispatcher(generation).ExecuteAsync(
                ["display-message", "-p", "ok"],
                TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => connection.ExecuteGuardedGroupAsync(
                generation,
                [["display-message", "-p", "one"], ["display-message", "-p", "two"]],
                TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Empty_namespace_normalizes_list_and_has_session_as_dead()
    {
        var connection = PsmuxConnection((request, _) =>
            Task.FromResult(Result(request.LogicalArguments)));

        TmuxCommandResult listed = await connection.ServerDispatcher.ExecuteAsync(
            ["list-sessions"],
            TestContext.Current.CancellationToken);
        TmuxCommandResult has = await connection.ServerDispatcher.ExecuteAsync(
            ["has-session", "-t", "alpha"],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, listed.ExitCode);
        Assert.Equal(1, has.ExitCode);
        Assert.Equal(
            ["no server running on selected psmux namespace"],
            listed.StandardErrorLines);
    }

    [Fact]
    public async Task Has_session_accepts_the_exact_name_marker()
    {
        var requests = new List<TmuxCommandRequest>();
        var connection = PsmuxConnection((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(OneSessionResult(request));
        });

        TmuxCommandResult result = await connection.ServerDispatcher.ExecuteAsync(
            ["has-session", "-t", "=alpha"],
            TestContext.Current.CancellationToken);
        await connection.ServerDispatcher.ExecuteAsync(
            ["has-session", "-t", "=$7"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            2,
            requests.Count(request => request.LogicalArguments.SequenceEqual(
                ["has-session", "-t", "=alpha"])));
    }

    [Fact]
    public async Task Mutating_commands_and_aliases_are_rejected_before_dispatch()
    {
        int calls = 0;
        var connection = PsmuxConnection((request, _) =>
        {
            calls++;
            return Task.FromResult(Result(request.LogicalArguments));
        });
        string[][] commands =
        [
            ["new-session", "-d", "-s", "alpha"],
            ["new", "-d", "-s", "alpha"],
            ["start-server"],
            ["kill-server", "-Z"],
            ["kill-ses", "-aC", "-t", "$7"],
            ["killw", "-a", "-t", "@1"],
            ["killp", "-a", "-t", "%1"],
            ["unlinkw", "-k", "-t", "@1"],
            ["respawnw", "-k", "-t", "@1"],
            ["respawnp", "-k", "-t", "%1"],
            ["detach"],
            ["rename-session", "-t", "$7", "renamed"],
            ["send-keys", "-t", "%1", "text"],
            ["set-option", "-g", "status", "off"],
            ["capture-pane", "-t", "%1", "-b", "buffer"],
            ["capture-pane", "-p", "-pb", "buffer"],
            ["capture-pane", "-p", "-N"],
            ["capture-pane", "-p", "-S", "0 -t beta"],
            ["capture-pane", "-p", "-E", "0\t-t\tbeta"],
            ["capture-pane", "-p", "-S", "00"],
            ["capture-pane", "-p", "-S", "0", "-S", "1"],
            ["capture-pane", "-p", "-p"],
            ["display-message", "-t", "%1", "message"],
            ["display-message", "-p", "-N", "message"],
            ["display-message", "-p", "-F", "#{pane_id}"],
            ["display-message", "-p", "-d", "1 -t beta", "message"],
            ["display-message", "-p", "-p", "message"],
            ["has-session", "-Z"],
            ["list-sessions", "-f", "#{session_attached}"],
            ["list-windows", "-f", "#{window_active}"],
            ["list-panes", "-Z"],
            ["list-panes", "-F#{pane_id}"],
            ["display-message", "-p", "#(cmd /c echo unsafe)"],
            ["list-sessions", "-F", "#(cmd /c echo unsafe)"],
            ["list-windows", "-F", "#(cmd /c echo unsafe)"],
            ["list-panes", "-F", "#(cmd /c echo unsafe)"],
            ["display-message", "-p", "#{E:unsafe}"],
            ["display-message", "-p", "#{T:unsafe}"],
            ["display-message", "-p", "#{Efoo:@unsafe}"],
            ["display-message", "-p", "#{Tfoo:@unsafe}"],
        ];

        foreach (string[] command in commands)
        {
            await Assert.ThrowsAsync<NotSupportedException>(
                () => connection.ServerDispatcher.ExecuteAsync(
                    command,
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Command_arguments_known_to_be_corrupted_are_rejected_before_dispatch()
    {
        int calls = 0;
        var connection = PsmuxConnection((request, _) =>
        {
            calls++;
            return Task.FromResult(Result(request.LogicalArguments));
        });

        string[] values =
        [
            string.Empty,
            "nul\0value",
            "cr\rvalue",
            "lf\nvalue",
            "single'quote",
            "double\"quote",
            "double\\\\slash",
            "trailing\\",
            "literal;",
            "0; kill-server",
            "a;b",
            "text ; kill-server",
            "text \\; kill-server",
        ];
        foreach (string value in values)
        {
            await Assert.ThrowsAsync<NotSupportedException>(
                () => connection.ServerDispatcher.ExecuteAsync(
                    ["display-message", "-p", value],
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Namespace_prefix_collisions_that_cannot_be_targeted_exactly_fail_closed()
    {
        int calls = 0;
        var connection = PsmuxConnection((request, _) =>
        {
            calls++;
            return Task.FromResult(request.LogicalArguments[0] == "list-sessions"
                ? Result(request.LogicalArguments, "41:100\t$7\tsecondary\n")
                : Result(
                    request.LogicalArguments,
                    standardError: "can't find session\n",
                    exitCode: 1));
        });

        await Assert.ThrowsAsync<NotSupportedException>(
            () => connection.ServerDispatcher.ExecuteAsync(
                ["list-sessions"],
                TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Psmux_preview_requires_an_explicit_namespace()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new PsmuxConnectionOptions(
                TestBinaryPath,
                TestBinarySha256,
                TestDataDirectory,
                ""));

        Assert.Equal("namespaceName", error.ParamName);
    }

    [Fact]
    public void Psmux_public_surface_omits_unbounded_connection_settings()
    {
        string[] names = typeof(PsmuxConnectionOptions)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("SocketPath", names);
        Assert.DoesNotContain("ConfigurationFile", names);
        Assert.DoesNotContain("ColorMode", names);
        Assert.DoesNotContain("ChildEnvironment", names);
        Assert.DoesNotContain("InitializeAsync", names);
    }

    [Fact]
    public void Psmux_public_surface_exposes_queries_only()
    {
        static string[] Methods(Type type) =>
        [
            .. type.GetMethods(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .Select(method => method.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            ["ConnectAsync", "GetPanesAsync", "GetSessionAsync", "GetWindowsAsync", "RefreshAsync"],
            Methods(typeof(PsmuxServer)));
        Assert.Equal(
            ["GetPanesAsync", "GetWindowsAsync"],
            Methods(typeof(PsmuxSession)));
        Assert.Equal(["GetPanesAsync"], Methods(typeof(PsmuxWindow)));
        Assert.Equal(["CaptureAsync"], Methods(typeof(PsmuxPane)));
    }

    [Fact]
    public async Task Psmux_facade_preserves_strict_transport_failures()
    {
        bool failFinalQuery = false;
        string[]? routedArguments = null;
        var connection = new TmuxConnection(
            PsmuxOptions(),
            (request, _) =>
            {
                if (failFinalQuery && request.LogicalArguments[0] == "list-windows")
                {
                    routedArguments = [.. request.LogicalArguments];
                    throw new TmuxTransportException(
                        "the verified client disappeared",
                        request.LogicalArguments,
                        TmuxDispatchState.NotDispatched);
                }

                return Task.FromResult(request.LogicalArguments[0] == "-V"
                    ? Result(request.LogicalArguments, AuditedBanner)
                    : OneSessionResult(request));
            },
            implementation: TmuxImplementation.Unknown);
        (ServerGeneration generation, string rawVersion) = await connection.DiscoverAsync(
            TestContext.Current.CancellationToken);
        var facade = new PsmuxServer(
            new PsmuxConnectionOptions(
                TestBinaryPath,
                TestBinarySha256,
                TestDataDirectory,
                TestNamespace),
            new Server(connection, generation, rawVersion));
        failFinalQuery = true;

        TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(
            () => facade.GetWindowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal("the verified client disappeared", error.Message);
        Assert.Equal(TmuxDispatchState.NotDispatched, error.Dispatch);
        Assert.NotNull(routedArguments);
        Assert.Equal(
            ["list-windows", "-a", "-F", routedArguments[^1]],
            error.Arguments);
        Assert.Equal(["list-windows", "-t", "alpha", "-F", routedArguments[^1]], routedArguments);
    }

    [Fact]
    public void Psmux_capture_options_map_only_the_audited_flags()
    {
        var options = new PsmuxCaptureOptions(
            startLine: new CapturePanePosition(-25),
            endLine: CapturePanePosition.EndOfVisiblePane,
            escapeSequences: true,
            joinWrappedLines: true);

        CapturePaneRequest request = options.ToRequest();

        Assert.Equal(-25, request.StartLine?.LineNumber);
        Assert.Null(request.EndLine?.LineNumber);
        Assert.True(request.EscapeSequences);
        Assert.True(request.JoinWrappedLines);
        Assert.False(request.AlternateScreen);
        Assert.False(request.Pending);
    }

    [Theory]
    [InlineData("3.3.6")]
    [InlineData("3.3.8")]
    public async Task Psmux_connection_rejects_versions_outside_the_audited_allowlist(
        string version)
    {
        int calls = 0;
        var connection = new TmuxConnection(
            PsmuxOptions(),
            (request, _) =>
            {
                calls++;
                return Task.FromResult(Result(
                    request.LogicalArguments,
                    $"tmux {version}\npsmux {version}\n"));
            },
            implementation: TmuxImplementation.Unknown);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => connection.DiscoverAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("psmux 3.3.8")]
    [InlineData("psmux 3.3.8 (05cc5d4 2026-07-20)")]
    [InlineData("psmux 3.3.8 (66cf613 2026-08-18 dirty)")]
    [InlineData("psmux 3.3.8 (66cf613 2026-08-18, dirty)")]
    public async Task Psmux_connection_requires_the_audited_build_provenance(string secondLine)
    {
        int calls = 0;
        var connection = new TmuxConnection(
            PsmuxOptions(),
            (request, _) =>
            {
                calls++;
                return Task.FromResult(Result(
                    request.LogicalArguments,
                    $"tmux 3.3.8\n{secondLine}\n"));
            },
            implementation: TmuxImplementation.Unknown);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => connection.DiscoverAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("unit__nested")]
    [InlineData("unit\0nested")]
    [InlineData("unit\rnested")]
    [InlineData("unit\nnested")]
    public void Psmux_rejects_ambiguous_or_unsafe_namespace_names(string socketName)
    {
        Assert.Throws<ArgumentException>(() => new PsmuxConnectionOptions(
            TestBinaryPath,
            TestBinarySha256,
            TestDataDirectory,
            socketName));
    }

    [Theory]
    [InlineData("child__alpha")]
    [InlineData("x ; kill-server")]
    public async Task Psmux_rejects_unsafe_session_names_before_targeted_probes(string sessionName)
    {
        int calls = 0;
        var connection = PsmuxConnection((request, _) =>
        {
            calls++;
            return Task.FromResult(Result(request.LogicalArguments, $"41:100\t$7\t{sessionName}\n"));
        });

        await Assert.ThrowsAsync<NotSupportedException>(
            () => connection.ServerDispatcher.ExecuteAsync(
                ["list-sessions"],
                TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    private static TmuxConnection PsmuxConnection(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute) =>
        new(
            PsmuxOptions(),
            execute,
            implementation: TmuxImplementation.Psmux);

    private static ServerConnectionOptions PsmuxOptions(
        string? binaryPath = null,
        string? dataDirectory = null,
        string? namespaceName = null) =>
        ServerConnectionOptions.ForPsmux(new PsmuxConnectionOptions(
            binaryPath ?? TestBinaryPath,
            TestBinarySha256,
            dataDirectory ?? TestDataDirectory,
            namespaceName ?? TestNamespace));

    private static bool IsGenerationProbe(TmuxCommandRequest request) =>
        request.LogicalArguments.SequenceEqual(
            ["display-message", "-p", "-t", "alpha", "#{pid}:#{start_time}\t#{session_id}\t#{session_name}"]);

    private static bool IsSelectedGenerationProbe(TmuxCommandRequest request) =>
        request.LogicalArguments.SequenceEqual(
            ["display-message", "-p", "#{pid}:#{start_time}\t#{session_id}\t#{session_name}"]);

    private static TmuxCommandResult OneSessionResult(
        TmuxCommandRequest request,
        string commandOutput = "")
    {
        IReadOnlyList<string> arguments = request.LogicalArguments;
        if (arguments[0] == "list-sessions")
        {
            return Result(arguments, "41:100\t$7\talpha\n");
        }

        if (IsGenerationProbe(request))
        {
            return Result(arguments, "41:100\t$7\talpha\n");
        }

        if (IsSelectedGenerationProbe(request))
        {
            return Result(arguments, "41:100\t$7\talpha\n");
        }

        if (arguments[0] == "list-panes")
        {
            return Result(arguments, "%1\n");
        }

        if (arguments[0] == "list-windows")
        {
            return Result(arguments, "@1\n");
        }

        return Result(arguments, commandOutput);
    }

    private static TmuxCommandResult Result(
        IReadOnlyList<string> arguments,
        string standardOutput = "",
        string standardError = "",
        int exitCode = 0)
    {
        byte[] output = Encoding.UTF8.GetBytes(standardOutput);
        byte[] error = Encoding.UTF8.GetBytes(standardError);
        return new TmuxCommandResult(
            arguments,
            exitCode,
            output,
            error,
            Utf8BackslashDecoder.ProjectOutputLines(output),
            Utf8BackslashDecoder.ProjectErrorLines(error));
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCalls;

        internal int PostCalls => Volatile.Read(ref _postCalls);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _postCalls);
            base.Post(callback, state);
        }
    }
}
