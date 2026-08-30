using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Entities;

[UnsupportedOSPlatform("windows")]
public sealed class CompositeMutationDispatchTests
{
    private static readonly ServerGeneration Generation = new(92, 902);

    [Fact]
    public async Task Layout_refresh_failure_is_unknown_after_the_layout_changed()
    {
        Window window = CreateWindow((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            if (ActualCommand(arguments) == ProjectionRead)
            {
                throw NotDispatched(arguments, "refresh was not dispatched");
            }

            return Task.FromResult(Success(request));
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            window.SelectLayoutAsync(
                new SelectLayoutRequest("tiled"),
                TestContext.Current.CancellationToken));

        AssertPartialFailure(failure, typeof(TmuxTransportException));
    }

    [Fact]
    public async Task Layout_cancellation_is_unknown_after_the_layout_changed()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Window window = CreateWindow((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("select-layout", StringComparer.Ordinal))
            {
                cancellation.Cancel();
            }

            return Task.FromResult(Success(request));
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            window.SelectLayoutAsync(new SelectLayoutRequest("tiled"), cancellation.Token));

        AssertPartialFailure(failure, typeof(OperationCanceledException));
    }

    [Fact]
    public async Task Layout_first_failure_keeps_not_dispatched()
    {
        Window window = CreateWindow((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            throw NotDispatched(arguments, "layout was not dispatched");
        });

        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(() =>
            window.SelectLayoutAsync(
                new SelectLayoutRequest("tiled"),
                TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.NotDispatched, failure.Dispatch);
        Assert.Equal("layout was not dispatched", failure.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0000")]
    [InlineData("0000x")]
    [InlineData("0000,")]
    public async Task Truncated_custom_layouts_are_refused_before_dispatch(string layout)
    {
        int dispatches = 0;
        Window window = CreateWindow((request, _) =>
        {
            Interlocked.Increment(ref dispatches);
            return Task.FromResult(Success(request));
        });

        await Assert.ThrowsAsync<TmuxWindowException>(() =>
            window.SelectLayoutAsync(
                new SelectLayoutRequest(layout),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, Volatile.Read(ref dispatches));
    }

    [Fact]
    public async Task Reset_second_mutation_failure_is_unknown()
    {
        int mutations = 0;
        Pane pane = CreatePane((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("send-keys", StringComparer.Ordinal)
                || arguments.Contains("clear-history", StringComparer.Ordinal))
            {
                if (Interlocked.Increment(ref mutations) == 2)
                {
                    throw NotDispatched(arguments, "clear was not dispatched");
                }
            }

            return Task.FromResult(Success(request));
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            pane.ResetAsync(TestContext.Current.CancellationToken));

        AssertPartialFailure(failure, typeof(TmuxTransportException));
        Assert.Equal(2, Volatile.Read(ref mutations));
    }

    [Fact]
    public async Task Appended_option_readback_failure_is_unknown()
    {
        Server server = CreateServer((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("show-options", StringComparer.Ordinal))
            {
                throw NotDispatched(arguments, "option readback was not dispatched");
            }

            return Task.FromResult(Success(request));
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            server.Options.SetAsync(
                new SetOptionRequest("status-left", "next", append: true),
                TestContext.Current.CancellationToken));

        AssertPartialFailure(failure, typeof(TmuxTransportException));
    }

    [Fact]
    public async Task Multi_hook_second_mutation_failure_is_unknown()
    {
        int mutations = 0;
        Server server = CreateServer((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("set-hook", StringComparer.Ordinal)
                && Interlocked.Increment(ref mutations) == 2)
            {
                throw NotDispatched(arguments, "second hook was not dispatched");
            }

            return Task.FromResult(Success(request));
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            server.Hooks.SetAsync(
                new SetHooksRequest(
                    "after-new-session",
                    new Dictionary<int, string>
                    {
                        [0] = "display-message first",
                        [1] = "display-message second",
                    }),
                TestContext.Current.CancellationToken));

        AssertPartialFailure(failure, typeof(TmuxTransportException));
        Assert.Equal(2, Volatile.Read(ref mutations));
    }

    [Fact]
    public async Task Replaced_session_listing_failure_is_unknown_after_creation()
    {
        var commands = new ConcurrentQueue<string>();
        Server server = CreateServer((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            string command = ActualCommand(arguments);
            commands.Enqueue(command);
            return command switch
            {
                "has-session" => Task.FromResult(Success(request)),
                "kill-session" => Task.FromResult(Success(request)),
                "new-session" => Task.FromResult(Success(request, "$2\n")),
                "display-message" => Task.FromResult(Success(
                    request,
                    $"{Generation.ProcessId}:{Generation.StartTime}\n")),
                "-V" => Task.FromResult(Success(request, "tmux 3.7\n")),
                ProjectionRead => throw NotDispatched(
                    arguments,
                    "session read was not dispatched"),
                _ => Task.FromResult(Success(request)),
            };
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            server.CreateSessionAsync(
                new NewSessionRequest("replace-me", replaceExisting: true),
                TestContext.Current.CancellationToken));

        AssertPartialFailure(failure, typeof(TmuxTransportException));
        Assert.Equal(
            [
                // The banner is read once, before the first command reaches tmux.
                "-V",
                "has-session",
                "kill-session",
                "new-session",
                "display-message",
                ProjectionRead,
            ],
            commands.ToArray());
    }

    [Fact]
    public async Task Malformed_created_identifier_is_unknown_after_creation()
    {
        Server server = CreateServer((request, _) =>
            Task.FromResult(Success(request, "not-a-session-id\n")));

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            server.CreateSessionAsync(
                new NewSessionRequest("created"),
                TestContext.Current.CancellationToken));

        AssertPartialFailure(failure, typeof(InvalidDataException));
    }

    [Fact]
    public async Task Select_existing_returns_the_expanded_name_match_when_detached()
    {
        var requests = new ConcurrentQueue<string[]>();
        TmuxVersion floor = TmuxVersion.Parse("3.2a");
        Session session = CreateSession((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            requests.Enqueue(arguments);
            string command = ActualCommand(arguments);
            return command switch
            {
                "-V" => Task.FromResult(Success(request)),
                "display-message" => Task.FromResult(Success(request, "-team-x\n")),
                "new-window" => Task.FromResult(Success(request)),
                "list-windows" => Task.FromResult(Success(
                    request,
                    WindowListing(
                        floor,
                        Generation,
                        ("@1", "active", true),
                        ("@2", "-team-x", false),
                        ("@3", "-#{session_name}-x", false)))),
                _ => throw new InvalidOperationException($"Unexpected command '{command}'."),
            };
        }, "tmux 3.2a");

        Window selected = await session.CreateWindowAsync(
            new NewWindowRequest("-#{session_name}-x", selectExisting: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(WindowId.Parse("@2"), selected.Id);
        Assert.Equal("-team-x", selected.Name);
        string[] expansion = requests.Single(arguments =>
            ActualCommand(arguments) == "display-message");
        Assert.Equal(
            ["display-message", "-p", "-t", "$1", "--", "-#{session_name}-x"],
            expansion[^6..]);
        string[] create = requests.Single(arguments =>
            ActualCommand(arguments) == "new-window");
        Assert.Contains("-d", create);
        Assert.Contains("-S", create);
        Assert.Contains("$1:", create);
    }

    [Fact]
    public async Task Window_scoped_create_does_not_treat_empty_output_as_selected_active()
    {
        Window window = CreateWindow((request, _) => Task.FromResult(Success(request)));

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            window.CreateWindowAsync(
                new NewWindowRequest("wanted", selectExisting: true),
                TestContext.Current.CancellationToken));

        AssertPartialFailure(failure, typeof(InvalidDataException));
    }

    [Fact]
    public async Task Environment_readback_command_failure_is_unknown_after_set()
    {
        Server server = CreateServer((request, _) =>
        {
            string command = ActualCommand([.. request.LogicalArguments]);
            return command == "show-environment"
                ? Task.FromResult(Failure(request, 2, "permission denied\n"))
                : Task.FromResult(Success(request));
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            server.Environment.SetAsync(
                "VISIBLE",
                "value",
                cancellationToken: TestContext.Current.CancellationToken));

        AssertPartialFailure(failure, typeof(TmuxCommandException));
    }

    [Fact]
    public async Task Environment_readback_stderr_is_unknown_even_with_zero_exit()
    {
        Server server = CreateServer((request, _) =>
        {
            string command = ActualCommand([.. request.LogicalArguments]);
            return command == "show-environment"
                ? Task.FromResult(Failure(request, 0, "readback warning\n"))
                : Task.FromResult(Success(request));
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            server.Environment.SetAsync(
                "HIDDEN",
                "value",
                hidden: true,
                cancellationToken: TestContext.Current.CancellationToken));

        AssertPartialFailure(failure, typeof(TmuxCommandException));
    }

    [Fact]
    public async Task Visible_environment_missing_after_set_is_unknown()
    {
        Server server = CreateServer((request, _) =>
        {
            string command = ActualCommand([.. request.LogicalArguments]);
            return command == "show-environment"
                ? Task.FromResult(Failure(request, 1, "unknown variable: VISIBLE\n"))
                : Task.FromResult(Success(request));
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            server.Environment.SetAsync(
                "VISIBLE",
                "value",
                cancellationToken: TestContext.Current.CancellationToken));

        AssertPartialFailure(failure, typeof(InvalidDataException));
    }

    [Fact]
    public async Task Exact_missing_environment_result_remains_an_absence_answer()
    {
        Server server = CreateServer((request, _) => Task.FromResult(
            request.LogicalArguments is ["-V"]
                ? Success(request)
                : Failure(request, 1, "unknown variable: MISSING\n")));

        TmuxEnvironmentEntry? entry = await server.Environment.GetAsync(
            "MISSING",
            TestContext.Current.CancellationToken);

        Assert.Null(entry);
    }

    [Fact]
    public async Task Session_create_reuses_unchanged_generation_without_reinitializing()
    {
        int initializations = 0;
        Server server = CreateServer(
            SessionCreationExecutor(Generation),
            (_, _) =>
            {
                Interlocked.Increment(ref initializations);
                return ValueTask.CompletedTask;
            });

        Session created = await server.CreateSessionAsync(
            new NewSessionRequest("created"),
            TestContext.Current.CancellationToken);

        Assert.Equal(Generation, created.Generation);
        Assert.Equal(0, Volatile.Read(ref initializations));
    }

    [Fact]
    public async Task Session_create_rediscovers_changed_generation_and_reinitializes()
    {
        var changed = new ServerGeneration(93, 903);
        int initializations = 0;
        Server server = CreateServer(
            SessionCreationExecutor(changed),
            (_, _) =>
            {
                Interlocked.Increment(ref initializations);
                return ValueTask.CompletedTask;
            });

        Session created = await server.CreateSessionAsync(
            new NewSessionRequest("created"),
            TestContext.Current.CancellationToken);

        Assert.Equal(changed, created.Generation);
        Assert.Equal(1, Volatile.Read(ref initializations));
    }

    private static void AssertPartialFailure(LibTmuxException failure, Type innerType)
    {
        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Equal(TmuxMutationSequence.PartialFailureMessage, failure.Message);
        Assert.IsType(innerType, failure.InnerException);
        if (failure.InnerException is TmuxTransportException inner)
        {
            Assert.Equal(TmuxDispatchState.NotDispatched, inner.Dispatch);
        }
    }

    private static Pane CreatePane(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute)
    {
        var connection = CreateConnection(execute);
        return new Pane(
            new Server(connection, Generation, "tmux 3.7"),
            connection,
            Generation,
            new PaneId(1));
    }

    private static Window CreateWindow(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute)
    {
        TmuxConnection connection = CreateConnection(execute);
        var server = new Server(connection, Generation, "tmux 3.7");
        return new Window(
            server,
            connection,
            Generation,
            new WindowId(1),
            new Dictionary<string, string?>
            {
                ["session_id"] = "$1",
                ["window_id"] = "@1",
            });
    }

    private static Session CreateSession(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
        string rawVersion = "tmux 3.7")
    {
        TmuxConnection connection = CreateConnection(execute);
        var server = new Server(connection, Generation, rawVersion);
        return new Session(
            server,
            connection,
            Generation,
            new SessionId(1),
            new Dictionary<string, string?>
            {
                ["session_id"] = "$1",
                ["session_name"] = "team",
            });
    }

    private static Server CreateServer(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
        Func<Server, CancellationToken, ValueTask>? initializeAsync = null)
    {
        TmuxConnection connection = CreateConnection(execute, initializeAsync);
        return new Server(connection, Generation, "tmux 3.7");
    }

    private static TmuxConnection CreateConnection(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
        Func<Server, CancellationToken, ValueTask>? initializeAsync = null) =>
        new(
            new ServerConnectionOptions(
                socketName: "composite-mutation-test",
                initializeAsync: initializeAsync),
            execute);

    private static TmuxTransportException NotDispatched(
        IReadOnlyList<string> arguments,
        string message) =>
        new(message, arguments, TmuxDispatchState.NotDispatched);

    // display-message serves three purposes: probing the generation, expanding
    // a format, and reading one entity. Only the read carries a framed template.
    private const string ProjectionRead = "read-one";

    private static string ActualCommand(string[] arguments)
    {
        string command = arguments.Contains("if-shell", StringComparer.Ordinal)
            ? arguments.Last(static argument => argument is
                "display-message" or "list-sessions" or "list-windows" or "list-panes"
                or "new-window")
            : arguments[0];
        return command == "display-message"
            && arguments[^1].Contains(FormatProjection.RowSeparator, StringComparison.Ordinal)
                ? ProjectionRead
                : command;
    }

    private static TmuxCommandResult Success(
        TmuxCommandRequest request,
        string payload = "",
        ServerGeneration? generation = null)
    {
        string[] arguments = [.. request.LogicalArguments];

        // Every connection reads the version banner once before its first
        // command, whatever else a test is scripting.
        if (arguments is ["-V"])
        {
            payload = "tmux 3.7\n";
        }

        bool guarded = arguments.Contains("if-shell", StringComparer.Ordinal);
        ServerGeneration effectiveGeneration = generation ?? Generation;
        string output = guarded
            ? $"{effectiveGeneration.ProcessId}:{effectiveGeneration.StartTime}\n{payload}"
            : payload;
        byte[] bytes = Encoding.UTF8.GetBytes(output);
        return new TmuxCommandResult(
            arguments,
            0,
            bytes,
            ReadOnlyMemory<byte>.Empty,
            Utf8BackslashDecoder.ProjectOutputLines(bytes),
            []);
    }

    private static TmuxCommandResult Failure(
        TmuxCommandRequest request,
        int exitCode,
        string standardError)
    {
        string[] arguments = [.. request.LogicalArguments];
        byte[] error = Encoding.UTF8.GetBytes(standardError);
        return new TmuxCommandResult(
            arguments,
            exitCode,
            ReadOnlyMemory<byte>.Empty,
            error,
            [],
            Utf8BackslashDecoder.ProjectErrorLines(error));
    }

    private static Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>
        SessionCreationExecutor(ServerGeneration discovered) =>
        (request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            string command = ActualCommand(arguments);
            return command switch
            {
                "new-session" => Task.FromResult(Success(request, "$2\n")),
                "display-message" => Task.FromResult(Success(
                    request,
                    $"{discovered.ProcessId}:{discovered.StartTime}\n")),
                "-V" => Task.FromResult(Success(request, "tmux 3.7\n")),
                ProjectionRead => Task.FromResult(Success(
                    request,
                    SessionListing(discovered, "$2", "created"),
                    discovered)),
                _ => throw new InvalidOperationException($"Unexpected command '{command}'."),
            };
        };

    private static string SessionListing(
        ServerGeneration generation,
        string id,
        string name) =>
        FramedListing(
            "list-sessions",
            TmuxVersion.Parse("3.7"),
            generation,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = id,
                ["session_name"] = name,
            });

    private static string WindowListing(
        TmuxVersion version,
        ServerGeneration generation,
        params (string Id, string Name, bool Active)[] windows) =>
        FramedListing(
            "list-windows",
            version,
            generation,
            [.. windows.Select((window, index) =>
                (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["session_id"] = "$1",
                    ["window_id"] = window.Id,
                    ["window_name"] = window.Name,
                    ["window_index"] = index.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["window_active"] = window.Active ? "1" : "0",
                })]);

    private static string FramedListing(
        string command,
        TmuxVersion version,
        ServerGeneration generation,
        params IReadOnlyDictionary<string, string>[] rows)
    {
        FormatProjection projection = FormatProjection.Create(command, version);
        return string.Concat(rows.Select(row =>
            string.Concat(projection.Fields.Select(field =>
                FieldValue(field.WireName, generation, row) + FormatProjection.RowSeparator))
            + "\n"));
    }

    private static string FieldValue(
        string field,
        ServerGeneration generation,
        IReadOnlyDictionary<string, string> row) =>
        field switch
        {
            "pid" => generation.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "start_time" => generation.StartTime.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            _ => row.TryGetValue(field, out string? value) ? value : string.Empty,
        };
}
