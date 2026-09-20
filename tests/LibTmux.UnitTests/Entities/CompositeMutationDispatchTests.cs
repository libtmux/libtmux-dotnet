using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Entities;

[UnsupportedOSPlatform("windows")]
public sealed class CompositeMutationDispatchTests
{
    private static readonly ServerGeneration Generation = new(92, 902);

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    public async Task Guarded_unlink_rejects_invalid_or_oversized_membership_before_dispatch(string input)
    {
        int dispatched = 0;
        Window window = CreateWindow((request, _) =>
        {
            dispatched++;
            return Task.FromResult(Success(request));
        });
        CancellationToken token = TestContext.Current.CancellationToken;

        PaneId[]? expected = input switch
        {
            "null" => null,
            "empty" => [],
            "duplicate" => [new PaneId(1), new PaneId(1)],
            _ => [.. Enumerable.Range(0, 4000).Select(value => new PaneId(value))],
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => window.UnlinkAsync(true, expected!, token));
        Assert.Equal(0, dispatched);
    }

    [Fact]
    public async Task Guarded_unlink_freezes_membership_in_the_generation_and_placement_queue()
    {
        List<PaneId> expected = [new(1), new(9)];
        TmuxCommandRequest? dispatched = null;
        Window window = CreateWindow((request, _) =>
        {
            expected.Clear();
            if (request.LogicalArguments.Contains("unlink-window", StringComparer.Ordinal))
            {
                dispatched = request;
            }
            return Task.FromResult(Success(request));
        });

        await window.UnlinkAsync(true, expected, TestContext.Current.CancellationToken);

        Assert.NotNull(dispatched);
        Assert.True(dispatched.PreventServerStart);
        Assert.Contains(dispatched.LogicalArguments, argument => argument.Contains("#{==:#{window_panes},2}", StringComparison.Ordinal)
            && argument.Contains("#{==:#{pane_id},%1}", StringComparison.Ordinal)
            && argument.Contains("#{==:#{pane_id},%9}", StringComparison.Ordinal));
        Assert.Equal(2, dispatched.LogicalArguments.Count(argument => argument == "if-shell"));
    }

    [Fact]
    public void Standalone_trim_is_a_generation_bound_command()
    {
        Pane pane = CreatePane((_, _) => throw new InvalidOperationException("Building reached tmux."));

        TmuxCommand command = new ResizePaneRequest { TrimBelow = true }.ToCommand(pane);

        Assert.Equal(["resize-pane", "-t", "%1", "-T"], command.ToArguments());
        Assert.Equal(Generation, command.RequiredGeneration);
    }

    [Fact]
    public async Task Trim_rejects_combined_modes_before_dispatch()
    {
        int dispatches = 0;
        Pane pane = CreatePane((request, _) =>
        {
            dispatches++;
            return Task.FromResult(Success(request));
        });
        ResizePaneRequest[] requests =
        [
            new() { TrimBelow = true, Width = "20" },
            new() { TrimBelow = true, Height = "10" },
            new() { TrimBelow = true, Zoom = true },
            new() { TrimBelow = true, Mouse = true },
            new() { TrimBelow = true, Direction = ResizeDirection.Up, Adjustment = 2 },
            new() { TrimBelow = true, Adjustment = 2 },
        ];

        foreach (ResizePaneRequest request in requests)
        {
            Assert.Throws<ArgumentException>(() => request.ToCommand(pane));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                pane.ResizeAsync(request, TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, dispatches);
    }

    [Fact]
    public async Task Move_inconsistent_readback_is_unknown_after_the_move_succeeded()
    {
        bool moved = false;
        Window window = CreateWindow((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("move-window", StringComparer.Ordinal))
            {
                moved = true;
            }

            return Task.FromResult(Success(request, ActualCommand(arguments) == "list-windows"
                ? MoveListing(moved ? [(3, "@1"), (5, "@1"), (8, "@2")] : [(0, "@1"), (5, "@1")])
                : string.Empty));
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            window.MoveAsync(new MoveWindowRequest
            {
                Destination = "3"
            }, TestContext.Current.CancellationToken));

        Assert.True(moved);
        AssertPartialFailure(failure, typeof(InvalidDataException));
    }

    [Fact]
    public async Task Move_missing_source_placement_does_not_move_a_surviving_sibling()
    {
        int moves = 0;
        Window window = CreateWindow((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("move-window", StringComparer.Ordinal))
            {
                moves++;
            }

            return Task.FromResult(Success(request, ActualCommand(arguments) == "list-windows"
                ? MoveListing([(0, "@2"), (5, "@1")])
                : string.Empty));
        });

        await Assert.ThrowsAsync<TmuxObjectNotFoundException>(() =>
            window.MoveAsync(new MoveWindowRequest
            {
                Destination = "3"
            }, TestContext.Current.CancellationToken));

        Assert.Equal(0, moves);
    }

    [Fact]
    public async Task Move_cancellation_after_dispatch_is_unknown()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Window window = CreateWindow((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("move-window", StringComparer.Ordinal))
            {
                cancellation.Cancel();
            }

            return Task.FromResult(Success(request, ActualCommand(arguments) == "list-windows"
                ? MoveListing([(0, "@1"), (5, "@1")])
                : string.Empty));
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            window.MoveAsync(new MoveWindowRequest
            {
                Destination = "3"
            }, cancellation.Token));

        AssertPartialFailure(failure, typeof(OperationCanceledException));
    }

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
                new SelectLayoutRequest { Layout = "tiled" },
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
            window.SelectLayoutAsync(new SelectLayoutRequest { Layout = "tiled" }, cancellation.Token));

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
                new SelectLayoutRequest { Layout = "tiled" },
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
                new SelectLayoutRequest { Layout = layout },
                TestContext.Current.CancellationToken));

        Assert.Equal(0, Volatile.Read(ref dispatches));
    }

    [Fact]
    public async Task Json_layouts_are_refused_before_dispatch_below_3_8()
    {
        int dispatches = 0;
        Window window = CreateWindow(
            (request, _) =>
            {
                Interlocked.Increment(ref dispatches);
                return Task.FromResult(Success(request));
            },
            rawVersion: "tmux 3.7c");

        // tmux's window_layout format became JSON at 3.8, so a JSON-shaped
        // layout is only trusted from a server that could have produced one.
        // Below that it is refused the same way an unknown name is: before
        // it ever reaches tmux.
        await Assert.ThrowsAsync<TmuxWindowException>(() =>
            window.SelectLayoutAsync(
                new SelectLayoutRequest { Layout = "{\"V\":2,\"L\":{\"t\":\"p\"}}" },
                TestContext.Current.CancellationToken));

        Assert.Equal(0, Volatile.Read(ref dispatches));
    }

    [Fact]
    public async Task Pane_display_message_client_flag_is_refused_before_3_3()
    {
        int dispatches = 0;
        Pane pane = CreatePane(
            (request, _) =>
            {
                Interlocked.Increment(ref dispatches);
                return Task.FromResult(Success(request));
            },
            rawVersion: "tmux 3.2a");

        TmuxVersionTooLowException failure = await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => pane.DisplayMessageAsync(
                new DisplayMessageRequest { Message = "#{pane_id}", TargetClient = "/dev/tty0" },
                TestContext.Current.CancellationToken));

        // tmux 3.2a declares -c as a bare flag with no value, so naming a
        // client is refused here rather than silently addressing a
        // different one. The flag takes a value starting at 3.3 itself
        // (tmux's own history: commit 4cc6db72 is tagged 3.3, 3.3a and
        // 3.4), not 3.3a.
        Assert.Equal(TmuxVersion.Parse("3.3"), failure.RequiredVersion);
        Assert.Equal(0, Volatile.Read(ref dispatches));
    }

    [Fact]
    public async Task Pane_display_message_client_flag_dispatches_from_3_3()
    {
        int dispatches = 0;
        Pane pane = CreatePane(
            (request, _) =>
            {
                Interlocked.Increment(ref dispatches);
                return Task.FromResult(Success(request));
            },
            rawVersion: "tmux 3.3");

        await pane.DisplayMessageAsync(
            new DisplayMessageRequest { Message = "#{pane_id}", TargetClient = "/dev/tty0" },
            TestContext.Current.CancellationToken);

        Assert.True(Volatile.Read(ref dispatches) > 0);
    }

    [Fact]
    public async Task Window_display_message_client_flag_is_refused_before_3_3()
    {
        int dispatches = 0;
        Window window = CreateWindow(
            (request, _) =>
            {
                Interlocked.Increment(ref dispatches);
                return Task.FromResult(Success(request));
            },
            rawVersion: "tmux 3.2a");

        TmuxVersionTooLowException failure = await Assert.ThrowsAsync<TmuxVersionTooLowException>(
            () => window.DisplayMessageAsync(
                new DisplayMessageRequest { Message = "#{window_id}", TargetClient = "/dev/tty0" },
                TestContext.Current.CancellationToken));

        Assert.Equal(TmuxVersion.Parse("3.3"), failure.RequiredVersion);
        Assert.Equal(0, Volatile.Read(ref dispatches));
    }

    [Fact]
    public async Task Window_display_message_client_flag_dispatches_from_3_3()
    {
        int dispatches = 0;
        Window window = CreateWindow(
            (request, _) =>
            {
                Interlocked.Increment(ref dispatches);
                return Task.FromResult(Success(request));
            },
            rawVersion: "tmux 3.3");

        await window.DisplayMessageAsync(
            new DisplayMessageRequest { Message = "#{window_id}", TargetClient = "/dev/tty0" },
            TestContext.Current.CancellationToken);

        Assert.True(Volatile.Read(ref dispatches) > 0);
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
                new SetOptionRequest("status-left", "next") { Append = true },
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
                "new-session" => Task.FromResult(Success(request,
                    $"{Generation.ProcessId}:{Generation.StartTime}\t$2\t@3\t%4\t0\n")),
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
                new NewSessionRequest { Name = "replace-me", ReplaceExisting = true },
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
                new NewSessionRequest { Name = "created" },
                TestContext.Current.CancellationToken));

        AssertPartialFailure(failure, typeof(TmuxCommandException));
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
            new NewWindowRequest { Name = "-#{session_name}-x", SelectExisting = true },
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
                new NewWindowRequest { Name = "wanted", SelectExisting = true },
                TestContext.Current.CancellationToken));

        AssertPartialFailure(failure, typeof(TmuxCommandException));
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

        AssertPartialFailure(failure, typeof(TmuxProtocolException));
    }

    [Fact]
    public async Task Environment_readback_distinguishes_dash_names_from_removed_variables()
    {
        Server server = CreateServer((request, _) => Task.FromResult(
            Success(request, "\n-\n-DASH=a=b\n-PLAIN\n--DASH\n")));

        IReadOnlyList<TmuxEnvironmentEntry> entries = await server.Environment.GetAllAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new TmuxEnvironmentEntry("-DASH", "a=b", false),
                new TmuxEnvironmentEntry("PLAIN", null, true),
                new TmuxEnvironmentEntry("-DASH", null, true),
            ],
            entries);
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
            new NewSessionRequest { Name = "created" },
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
            new NewSessionRequest { Name = "created" },
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

        // An unreadable answer still came from tmux, so it carries what tmux
        // was asked and what it said.
        if (failure.InnerException is TmuxCommandException answered)
        {
            Assert.Equal(TmuxDispatchState.Dispatched, answered.Dispatch);
            Assert.NotEmpty(answered.Result.Arguments);
        }
    }

    private static Pane CreatePane(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
        string rawVersion = "tmux 3.7")
    {
        var connection = CreateConnection(execute);
        return new Pane(
            new Server(connection, Generation, rawVersion),
            connection,
            Generation,
            new PaneId(1),
            new Dictionary<string, string?>());
    }

    private static Window CreateWindow(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
        string rawVersion = "tmux 3.7")
    {
        TmuxConnection connection = CreateConnection(execute);
        var server = new Server(connection, Generation, rawVersion);
        return new Window(
            server,
            connection,
            Generation,
            new WindowId(1),
            new Dictionary<string, string?>
            {
                ["session_id"] = "$1",
                ["window_id"] = "@1",
                ["window_index"] = "0",
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
            new ServerConnectionOptions
            {
                SocketName = "composite-mutation-test",
                InitializeAsync = initializeAsync,
            },
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
                "new-session" => Task.FromResult(Success(request,
                    $"{discovered.ProcessId}:{discovered.StartTime}\t$2\t@3\t%4\t0\n")),
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

    private static string MoveListing((int Index, string Id)[] windows) =>
        FramedListing(
            "list-windows",
            TmuxVersion.Parse("3.7"),
            Generation,
            [.. windows.Select((window, ordinal) =>
                (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["session_id"] = "$1",
                    ["window_id"] = window.Id,
                    ["window_index"] = window.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["window_active"] = ordinal == 0 ? "1" : "0",
                })]);

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
