using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

using LibTmux.UnitTests.Connection;

namespace LibTmux.UnitTests.Entities;

[UnsupportedOSPlatform("windows")]
public sealed class PaneSendKeysDispatchTests
{
    private static readonly ServerGeneration Generation = new(91, 901);

    [Fact]
    public void A_send_and_wait_judges_later_output_without_the_typed_echo()
    {
        const string typed = "true && true && echo MARKER";
        Func<IReadOnlyList<string>, IReadOnlyList<string>> withoutEcho = PaneText.TypedEchoRemover(typed);

        // A narrow pane wraps the typed line, once mid-word and once where
        // tmux trims the space a row ends with; the last row alone holds the pattern.
        string[] echoed = ["$ true && tr", "ue &&", "echo MARKER"];

        Assert.DoesNotContain("MARKER", string.Join('\n', withoutEcho(["$ " + typed])), StringComparison.Ordinal);
        Assert.DoesNotContain("MARKER", string.Join('\n', withoutEcho(echoed)), StringComparison.Ordinal);
        Assert.Equal("MARKER", withoutEcho([.. echoed, "MARKER"])[^1]);
    }

    [Fact]
    public void Wrapped_echo_remains_removable_when_tmux_drops_more_spaces_than_it_adds_breaks()
    {
        Func<IReadOnlyList<string>, IReadOnlyList<string>> withoutEcho =
            PaneText.TypedEchoRemover("echo  done");

        Assert.Equal(["", ""], withoutEcho(["echo", "done"]));
    }

    [Fact]
    public void A_send_and_wait_discounts_an_echo_in_progress_or_with_trailing_spaces()
    {
        Func<IReadOnlyList<string>, IReadOnlyList<string>> withoutEcho =
            PaneText.TypedEchoRemover("echo done; sleep 5 ");

        // The shell has echoed only part of the line, which already holds the pattern.
        Assert.DoesNotContain("done", string.Join('\n', withoutEcho(["$ echo done; sl"])), StringComparison.Ordinal);
        Assert.DoesNotContain("done", string.Join('\n', withoutEcho(["$ echo done; sleep 5", ""])), StringComparison.Ordinal);
        Assert.Equal("done", withoutEcho(["$ echo done; sleep 5", "done"])[^1]);
    }

    [Fact]
    public void Typed_echo_projection_rejects_input_beyond_its_UTF8_budget()
    {
        // Fewer than 64 KiB characters can still exceed a 64 KiB byte budget.
        string typed = new('\u754c', 21_846);

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => PaneText.TypedEchoRemover(typed));

        Assert.Equal("typed", failure.ParamName);
    }

    [Fact]
    public async Task Triggered_wait_rejects_oversize_typed_text_before_pane_io()
    {
        int calls = 0;
        Pane pane = CreatePane((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Validation reached tmux.");
        });

        ArgumentException failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            pane.SendKeysAndWaitAsync(
                new SendKeysRequest { Text = new string('\u754c', 21_846), Literal = true },
                PaneWaitRequest.FromTextPatterns(["ready"]),
                TestContext.Current.CancellationToken));

        Assert.Equal("typed", failure.ParamName);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Typed_echo_projection_accepts_the_UTF8_budget_boundary()
    {
        string typed = new('\u754c', 21_845);
        Func<IReadOnlyList<string>, IReadOnlyList<string>> withoutEcho =
            PaneText.TypedEchoRemover(typed);

        Assert.Equal(["ready"], withoutEcho(["ready"]));
    }

    [Fact]
    public void Admitted_64KiB_wrapped_echo_is_removed_without_losing_rows()
    {
        string typed = "b " + new string('a', 65_534);
        string[] rows = [.. typed.Chunk(80).Select(chunk => new string(chunk))];

        IReadOnlyList<string> projected = PaneText.TypedEchoRemover(typed)(rows);

        Assert.Equal(rows.Length, projected.Count);
        Assert.All(projected, Assert.Empty);
    }

    [Fact]
    public void Admitted_64KiB_partial_wrapped_echo_is_removed_without_losing_rows()
    {
        string typed = "b " + new string('a', 65_534);
        string[] rows = [.. typed[..^1].Chunk(80).Select(chunk => new string(chunk))];

        IReadOnlyList<string> projected = PaneText.TypedEchoRemover(typed)(rows);

        Assert.Equal(rows.Length, projected.Count);
        Assert.All(projected, Assert.Empty);
    }

    [Fact]
    public void Admitted_64KiB_repeated_nonword_partial_echo_is_removed()
    {
        string typed = new('-', 65_536);

        IReadOnlyList<string> projected = PaneText.TypedEchoRemover(typed)([typed[..^1]]);

        Assert.Equal([""], projected);
    }

    [Fact]
    public void Admitted_long_typed_text_without_an_echo_keeps_the_screen()
    {
        string typed = "b" + new string('a', 65_535);
        Func<IReadOnlyList<string>, IReadOnlyList<string>> withoutEcho =
            PaneText.TypedEchoRemover(typed);
        string[] screen = [new string('a', 4096)];

        Assert.Equal(screen, withoutEcho(screen));

        string mixed = "b " + new string('a', 65_534);
        string[] wrapped = ["b unrelated", new string('a', 4096), new string('a', 4096)];
        Assert.Equal(wrapped, PaneText.TypedEchoRemover(mixed)(wrapped));
    }

    [Theory]
    [InlineData("a \nb", false)]
    [InlineData("a\n b", true)]
    [InlineData("a\n  b", false)]
    public void Partial_echo_projection_preserves_wrapped_space_boundaries(
        string captured,
        bool removed)
    {
        Func<IReadOnlyList<string>, IReadOnlyList<string>> withoutEcho =
            PaneText.TypedEchoRemover("a bc");

        IReadOnlyList<string> projected = withoutEcho(captured.Split('\n'));
        string[] expected = removed ? ["", ""] : captured.Split('\n');

        Assert.Equal(expected, projected);
    }

    [Fact]
    public void Typed_echo_projection_rejects_captured_rows_beyond_the_match_work_budget()
    {
        Func<IReadOnlyList<string>, IReadOnlyList<string>> withoutEcho =
            PaneText.TypedEchoRemover("not present");
        string row = new('a', 4_194_304);

        Assert.Throws<PaneTextWaiter.MatchWorkExceededException>(
            () => withoutEcho([row, row]));
    }

    [Fact]
    public void Typed_echo_projection_observes_cancellation_during_partial_echo_work()
    {
        using CancellationTokenSource cancellation = new();
        int checks = 0;
        PaneText.TypedEchoProjection projection = new(
            "b" + new string('a', 8192),
            () =>
            {
                if (++checks == 10)
                {
                    cancellation.Cancel();
                }

                return TimeSpan.FromSeconds(1);
            }, cancellation.Token);

        Assert.Throws<OperationCanceledException>(() => projection.Project([new string('a', 4096)]));
        Assert.True(checks >= 10);
    }

    [Fact]
    public void Echo_removal_retries_after_an_occurrence_inside_a_longer_word()
    {
        Func<IReadOnlyList<string>, IReadOnlyList<string>> withoutEcho = PaneText.TypedEchoRemover("x x");

        Assert.Equal(["ax  ok"], withoutEcho(["ax x x ok"]));
    }

    [Fact]
    public void A_composite_request_cannot_be_reduced_to_one_command()
    {
        Pane pane = CreatePane((_, _) => throw new InvalidOperationException("Building reached tmux."));

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => new SendKeysRequest
            {
                Text = "payload",
                Enter = true,
                Literal = true
            }.ToCommand(pane));

        Assert.Contains("ToCommands", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_typed_request_dispatches_text_and_enter_in_order()
    {
        var dispatched = new ConcurrentQueue<string[]>();
        Pane pane = CreatePane((request, _) =>
        {
            dispatched.Enqueue([.. request.LogicalArguments]);
            return Task.FromResult(Success(request.LogicalArguments));
        });

        TmuxCommandResult result = await new SendKeysRequest
        {
            Text = "payload",
            Enter = true,
            Literal = true
        }
            .ExecuteAsync(pane, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        string[] sent = Assert.Single(dispatched);
        int commandStart = Array.IndexOf(sent, "send-keys");
        Assert.Equal(
            ["send-keys", "-t", "%1", "-l", "--", "payload", ";", "send-keys", "-t", "%1", "Enter"],
            sent[commandStart..]);
    }

    [Fact]
    public void Requests_without_following_enter_keep_a_single_command()
    {
        Pane pane = CreatePane((_, _) => throw new InvalidOperationException("Building reached tmux."));
        SendKeysRequest[] requests =
        [
            new SendKeysRequest { Text = "payload", Enter = false, Literal = true },
            new SendKeysRequest { CopyModeCommand = "cancel" },
            new SendKeysRequest { Reset = true },
            new SendKeysRequest { Repeat = 2 },
        ];
        foreach (SendKeysRequest request in requests)
        {
            TmuxCommand command = Assert.Single(request.ToCommands(pane));
            Assert.Equal(command, request.ToCommand(pane));
            Assert.Equal(Generation, command.RequiredGeneration);
        }

        Assert.Throws<ArgumentException>(() => new SendKeysRequest().ToCommands(pane));
    }

    [Fact]
    public async Task Control_replies_are_concatenated_in_command_order()
    {
        var sent = new List<TmuxCommand>();
        Pane pane = CreatePane((_, _) => throw new InvalidOperationException("Control used the process transport."));
        await using var control = new FakeControlSession((command, _) =>
        {
            sent.Add(command);
            return Task.FromResult<IReadOnlyList<string>>([sent.Count == 1 ? "text reply" : "Enter reply"]);
        });

        IReadOnlyList<string> lines = await new SendKeysRequest
        {
            Text = "Enter",
            Literal = true
        }
            .ExecuteAsync(pane, control, TestContext.Current.CancellationToken);

        Assert.Equal(["text reply", "Enter reply"], lines);
        Assert.Equal(2, sent.Count);
        Assert.Equal(["-t", "%1", "-l", "--", "Enter"], sent[0].Arguments);
        Assert.Equal(["-t", "%1", "Enter"], sent[1].Arguments);
        Assert.All(sent, command => Assert.Equal(Generation, command.RequiredGeneration));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Control_partial_failures_preserve_dispatch_uncertainty(int failAt, bool cancel)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Exception cause = cancel
            ? new OperationCanceledException(cancellation.Token)
            : new TmuxTransportException("The command was not dispatched.", [], TmuxDispatchState.NotDispatched);
        int calls = 0;
        Pane pane = CreatePane((_, _) => throw new InvalidOperationException("Control used the process transport."));
        await using var control = new FakeControlSession((_, _) =>
        {
            if (++calls == failAt)
            {
                cancellation.Cancel();
                return Task.FromException<IReadOnlyList<string>>(cause);
            }

            return Task.FromResult<IReadOnlyList<string>>([]);
        });

        Exception? failure = await Record.ExceptionAsync(() => new SendKeysRequest
        {
            Text = "payload",
            Literal = true
        }
            .ExecuteAsync(pane, control, cancellation.Token));

        Assert.Equal(failAt, calls);
        if (failAt == 1)
        {
            Assert.Same(cause, failure);
        }
        else
        {
            LibTmuxException partial = Assert.IsType<LibTmuxException>(failure);
            Assert.Equal(TmuxDispatchState.Unknown, partial.Dispatch);
            Assert.Same(cause, partial.InnerException);
            Assert.Contains("do not retry", partial.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Send_text_uses_literal_mode()
    {
        var dispatched = new ConcurrentQueue<string[]>();
        Pane pane = CreatePane((request, _) =>
        {
            dispatched.Enqueue([.. request.LogicalArguments]);
            return Task.FromResult(Success(request.LogicalArguments));
        });

        await pane.SendTextAsync(
            "Enter",
            enter: false,
            TestContext.Current.CancellationToken);

        string[] sent = Assert.Single(dispatched);
        int commandStart = Array.IndexOf(sent, "send-keys");
        Assert.NotEqual(-1, commandStart);
        Assert.Equal(["send-keys", "-t", "%1", "-l", "--", "Enter"], sent[commandStart..]);
    }

    [Fact]
    public async Task Nul_text_is_rejected_before_any_dispatch()
    {
        int calls = 0;
        Pane pane = CreatePane((request, _) =>
        {
            calls++;
            return Task.FromResult(Success(request.LogicalArguments));
        });

        await Assert.ThrowsAsync<ArgumentException>(() => pane.SendTextAsync(
            "before\0after", enter: false, TestContext.Current.CancellationToken));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Enter_not_dispatched_after_text_is_reported_as_unknown()
    {
        var dispatched = new ConcurrentQueue<string[]>();
        var enterFailure = new TmuxTransportException(
            "Enter was not dispatched.",
            ["send-keys", "-t", "%1", "Enter"],
            TmuxDispatchState.NotDispatched);
        Pane pane = CreatePane((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("send-keys", StringComparer.Ordinal))
            {
                dispatched.Enqueue(arguments);
                if (dispatched.Count == 2)
                {
                    throw enterFailure;
                }
            }

            return Task.FromResult(Success(arguments));
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            pane.SendKeysAsync(
                new SendKeysRequest { Text = "payload", Enter = true, Literal = true },
                TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        TmuxTransportException inner = Assert.IsType<TmuxTransportException>(
            failure.InnerException);
        Assert.Equal(TmuxDispatchState.NotDispatched, inner.Dispatch);
        Assert.Contains("text was sent", failure.Message, StringComparison.Ordinal);
        Assert.Contains("do not retry", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, dispatched.Count);
        Assert.Equal("Enter", dispatched.Last()[^1]);
    }

    [Fact]
    public async Task Cancellation_between_text_and_enter_is_reported_as_unknown()
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        int sendStages = 0;
        OperationCanceledException? enterFailure = null;
        Pane pane = CreatePane(async (request, cancellationToken) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("send-keys", StringComparer.Ordinal))
            {
                int stage = Interlocked.Increment(ref sendStages);
                if (stage == 1)
                {
                    await cancellation.CancelAsync();
                }
                else
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException error)
                    {
                        enterFailure = error;
                        throw;
                    }
                }
            }

            return Success(arguments);
        });

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            pane.SendKeysAsync(
                new SendKeysRequest { Text = "payload", Enter = true, Literal = true },
                cancellation.Token));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Same(enterFailure, failure.InnerException);
        Assert.Equal(2, Volatile.Read(ref sendStages));
    }

    [Fact]
    public async Task Text_stage_failure_keeps_its_not_dispatched_state()
    {
        int sendStages = 0;
        Pane pane = CreatePane((request, _) =>
        {
            string[] arguments = [.. request.LogicalArguments];
            if (arguments.Contains("send-keys", StringComparer.Ordinal))
            {
                Interlocked.Increment(ref sendStages);
                throw new TmuxTransportException(
                    "Text was not dispatched.",
                    arguments,
                    TmuxDispatchState.NotDispatched);
            }

            return Task.FromResult(Success(arguments));
        });

        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(() =>
            pane.SendKeysAsync(
                new SendKeysRequest { Text = "payload", Enter = true, Literal = true },
                TestContext.Current.CancellationToken));

        Assert.Equal(TmuxDispatchState.NotDispatched, failure.Dispatch);
        Assert.Equal(1, Volatile.Read(ref sendStages));
    }

    private static Pane CreatePane(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute)
    {
        var connection = new TmuxConnection(
            new ServerConnectionOptions { SocketName = "send-keys-dispatch-test" },
            FakeMultiplexer.AnsweringVersion(execute));
        return new Pane(
            new Server(connection, Generation, "tmux 3.7"),
            connection,
            Generation,
            new PaneId(1),
            new Dictionary<string, string?>());
    }

    private static TmuxCommandResult Success(IReadOnlyList<string> arguments)
    {
        byte[] output = Encoding.UTF8.GetBytes(
            $"{Generation.ProcessId}:{Generation.StartTime}\n");
        return new TmuxCommandResult(
            arguments,
            0,
            output,
            ReadOnlyMemory<byte>.Empty,
            Utf8BackslashDecoder.ProjectOutputLines(output),
            []);
    }

    private sealed class FakeControlSession(
        Func<TmuxCommand, CancellationToken, Task<IReadOnlyList<string>>> send) : IControlModeSession
    {
        public IAsyncEnumerable<TmuxEvent> Events => throw new NotSupportedException();

        public bool IsRunning => true;

        public Task<IReadOnlyList<string>> SendAsync(
            TmuxCommand command,
            CancellationToken cancellationToken = default) => send(command, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
