using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Connection;

[UnsupportedOSPlatform("windows")]
public sealed class AttachmentCompletionTests
{
    private static readonly ServerGeneration Generation = new(42, 100);
    private static readonly TimeSpan MissingReceiptBudget = TimeSpan.FromMilliseconds(30);
    private static readonly string[] Arguments = ["attach-session", "-t", "$2"];

    [ConnectionUnixFact]
    public async Task A_positive_receipt_allows_empty_output_and_preserves_the_exact_guard()
    {
        var fixture = new Fixture();
        fixture.Attach = (request, _) =>
        {
            fixture.Signal();
            return Task.FromResult(Result(request, string.Empty));
        };

        TmuxCommandResult result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Arguments, result.Arguments);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput.ToArray());
        TmuxCommandRequest attach = Assert.Single(fixture.Requests,
            request => request.LogicalArguments.Contains("attach-session", StringComparer.Ordinal));
        Assert.True(attach.PreventServerStart);
        Assert.NotNull(fixture.Channel);
        Assert.Equal(["display-message", "-p", TmuxConnection.GenerationFormat, ";", "if-shell", "-F",
            "#{==:#{pid}:#{start_time},42:100}", string.Empty, "stale_marker", ";",
            .. Arguments, ";", "wait-for", "-S", fixture.Channel], attach.LogicalArguments);
        Assert.Equal(0, fixture.Withdrawals);
    }

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(ConnectionUnixEnvironment),
        SkipUnless = nameof(ConnectionUnixEnvironment.IsUnix))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_receipts_fail_even_when_withdrawal_or_client_cancellation_signals(bool clientStillRunning)
    {
        var fixture = new Fixture { AcknowledgementBudget = MissingReceiptBudget };
        fixture.Attach = async (request, token) =>
        {
            if (clientStillRunning)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    fixture.Signal();
                    fixture.ClientCancelled = true;
                    throw;
                }
            }
            return Result(request, string.Empty);
        };

        TmuxProtocolException failure = await Assert.ThrowsAsync<TmuxProtocolException>(fixture.ExecuteAsync);

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.Contains("acknowledg", failure.Message, StringComparison.Ordinal);
        Assert.Equal(clientStillRunning, fixture.ClientCancelled);
        Assert.True(fixture.WaiterFinished);
        if (!clientStillRunning)
        {
            Assert.Equal(1, fixture.Withdrawals);
        }
    }

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(ConnectionUnixEnvironment),
        SkipUnless = nameof(ConnectionUnixEnvironment.IsUnix))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_errors_and_stale_generations_do_not_wait_for_positive_receipts(bool stale)
    {
        var fixture = new Fixture();
        fixture.Attach = (request, _) => Task.FromResult(Result(request,
            stale ? "42:101\n" : "42:100\n", 1,
            stale ? "unknown command: stale_marker\n" : "open terminal failed: not a terminal\n"));

        if (stale)
        {
            StaleServerGenerationException failure = await Assert.ThrowsAsync<StaleServerGenerationException>(fixture.ExecuteAsync);
            Assert.Equal(new ServerGeneration(42, 101), failure.Actual);
        }
        else
        {
            TmuxCommandResult result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, result.ExitCode);
            Assert.Equal(Arguments, result.Arguments);
            Assert.Equal(["open terminal failed: not a terminal"], result.StandardErrorLines);
        }
        Assert.Equal(1, fixture.Withdrawals);
        Assert.True(fixture.WaiterFinished);
    }

    [ConnectionUnixFact]
    public async Task Receipt_does_not_turn_nonzero_exit_or_malformed_output_into_success()
    {
        var fixture = new Fixture();
        fixture.Attach = (request, _) =>
        {
            fixture.Signal();
            return Task.FromResult(Result(request, "bad framing"));
        };
        await Assert.ThrowsAsync<TmuxCommandException>(fixture.ExecuteAsync);

        fixture = new Fixture();
        fixture.Attach = (request, _) =>
        {
            fixture.Signal();
            return Task.FromResult(Result(request, string.Empty, 1, "attach failed\n"));
        };
        Assert.Equal(1, (await fixture.ExecuteAsync(TestContext.Current.CancellationToken)).ExitCode);
    }

    [Theory(Skip = "Requires a Unix process environment.", SkipType = typeof(ConnectionUnixEnvironment),
        SkipUnless = nameof(ConnectionUnixEnvironment.IsUnix))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_wins_before_or_after_receipt(bool acknowledged)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var fixture = new Fixture();
        fixture.Attach = async (_, token) =>
        {
            if (acknowledged)
            {
                fixture.Signal();
            }
            await caller.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancellation did not interrupt attachment.");
        };

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.ExecuteAsync(caller.Token));

        Assert.True(failure.CancellationToken.IsCancellationRequested);
        Assert.True(fixture.WaiterFinished);
    }

    [ConnectionUnixFact]
    public async Task A_cancelled_caller_dispatches_neither_waiter_nor_attachment()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        var fixture = new Fixture();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ExecuteAsync(caller.Token));

        Assert.Empty(fixture.Requests);
    }

    [ConnectionUnixFact]
    public async Task Withdrawal_failure_keeps_the_missing_receipt_as_the_original_cause()
    {
        var fixture = new Fixture { FailWithdrawal = true, AcknowledgementBudget = MissingReceiptBudget };

        LibTmuxException failure = await Assert.ThrowsAnyAsync<LibTmuxException>(fixture.ExecuteAsync);

        AggregateException causes = Assert.IsType<AggregateException>(failure.InnerException);
        Assert.IsType<TmuxProtocolException>(causes.InnerExceptions[0]);
        Assert.Contains("withdraw", causes.InnerExceptions[1].Message, StringComparison.Ordinal);
        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.True(fixture.WaiterFinished);
    }

    [ConnectionUnixFact]
    public async Task A_stale_receipt_waiter_preserves_the_rejection_without_attachment_or_withdrawal()
    {
        var fixture = new Fixture { RejectWaitGeneration = true };

        StaleServerGenerationException failure = await Assert.ThrowsAsync<StaleServerGenerationException>(fixture.ExecuteAsync);

        Assert.Equal(new ServerGeneration(42, 101), failure.Actual);
        Assert.Equal(0, fixture.Withdrawals);
        Assert.DoesNotContain(fixture.Requests,
            request => request.LogicalArguments.Contains("attach-session", StringComparer.Ordinal));
    }

    [ConnectionUnixFact]
    public async Task A_signalled_receipt_does_not_override_the_pending_client_result()
    {
        var fixture = new Fixture();
        var releaseClient = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Attach = async (request, token) =>
        {
            fixture.Signal();
            await releaseClient.Task.WaitAsync(token);
            return Result(request, string.Empty, 7, "later client failure\n");
        };

        Task<TmuxCommandResult> execution = fixture.ExecuteAsync(TestContext.Current.CancellationToken);
        await fixture.WaiterCompleted.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(execution.IsCompleted);
        }
        finally
        {
            releaseClient.SetResult();
        }
        TmuxCommandResult result = await execution;

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(["later client failure"], result.StandardErrorLines);
        Assert.Equal(0, fixture.Withdrawals);
    }

    [ConnectionUnixFact]
    public async Task Generic_guard_execution_still_rejects_empty_success_output()
    {
        var guard = new TmuxGenerationGuard(
            static (request, _) => Task.FromResult(Result(request, string.Empty)),
            static () => "stale_marker");

        await Assert.ThrowsAsync<TmuxCommandException>(() => guard.ExecuteAsync(
            Generation, [Arguments], TestContext.Current.CancellationToken));
    }

    [ConnectionUnixFact]
    public async Task Configured_command_timeout_still_bounds_an_acknowledged_attachment()
    {
        var fixture = new Fixture();
        fixture.Attach = async (_, token) =>
        {
            fixture.Signal();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The command timeout was not applied.");
        };
        var connection = new TmuxConnection(new ServerConnectionOptions { CommandTimeout = TimeSpan.FromMilliseconds(250) },
            FakeMultiplexer.AnsweringVersion(fixture.SendAsync), static () => "stale_marker");
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));

        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(() =>
            connection.CreateAttachmentDispatcher(Generation).ExecuteAsync(Arguments, watchdog.Token));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.False(watchdog.IsCancellationRequested);
        Assert.True(fixture.WaiterFinished);
    }

    [ConnectionUnixFact]
    public async Task Client_cancellation_callback_failure_does_not_skip_receipt_withdrawal()
    {
        var fixture = new Fixture { AcknowledgementBudget = MissingReceiptBudget };
        fixture.Attach = async (_, token) =>
        {
            var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                callbackStarted.SetResult();
                throw new InvalidOperationException("callback failed");
            });
            await callbackStarted.Task;
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The client was not cancelled.");
        };

        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(fixture.ExecuteAsync);

        AggregateException causes = Assert.IsType<AggregateException>(failure.InnerException);
        Assert.IsType<TmuxProtocolException>(causes.InnerExceptions[0]);
        Assert.Equal(1, fixture.Withdrawals);
        Assert.True(fixture.WaiterFinished);
    }

    [ConnectionUnixFact]
    public async Task Receipt_cancellation_preserves_started_client_execution_metadata()
    {
        var fixture = new Fixture { CancelReceipt = true };
        fixture.Attach = async (_, token) =>
        {
            fixture.Signal();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException error)
            {
                throw new TmuxOperationCanceledException("The owned client was cancelled.", token, true, 123, error);
            }
            throw new InvalidOperationException("The owned client was not stopped.");
        };

        TmuxOperationCanceledException failure = await Assert.ThrowsAsync<TmuxOperationCanceledException>(fixture.ExecuteAsync);

        Assert.True(failure.CommandMayHaveExecuted);
        Assert.Equal(123, failure.ClientProcessId);
        Assert.True(fixture.WaiterFinished);
    }

    private static TmuxCommandResult Result(TmuxCommandRequest request, string output, int exitCode = 0, string error = "")
    {
        byte[] stdout = Encoding.UTF8.GetBytes(output);
        byte[] stderr = Encoding.UTF8.GetBytes(error);
        return new TmuxCommandResult(request.LogicalArguments, exitCode, stdout, stderr,
            Utf8BackslashDecoder.ProjectOutputLines(stdout), Utf8BackslashDecoder.ProjectErrorLines(stderr));
    }

    private sealed class Fixture
    {
        private readonly TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource waiterCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TimeSpan AcknowledgementBudget { get; init; } = TmuxGenerationGuard.AttachmentAcknowledgementBudget;
        internal Task WaiterCompleted => waiterCompleted.Task;
        internal List<TmuxCommandRequest> Requests { get; } = [];
        internal Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> Attach { get; set; } =
            static (request, _) => Task.FromResult(Result(request, string.Empty));
        internal string? Channel { get; private set; }
        internal int Withdrawals { get; private set; }
        internal bool ClientCancelled { get; set; }
        internal bool WaiterFinished { get; private set; }
        internal bool FailWithdrawal { get; init; }
        internal bool RejectWaitGeneration { get; init; }
        internal bool CancelReceipt { get; init; }
        internal void Signal() => signal.TrySetResult();
        internal Task<TmuxCommandResult> ExecuteAsync() => ExecuteAsync(TestContext.Current.CancellationToken);
        internal async Task<TmuxCommandResult> ExecuteAsync(CancellationToken token)
        {
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
            watchdog.CancelAfter(TimeSpan.FromSeconds(10));
            return await new TmuxGenerationGuard(SendAsync, static () => "stale_marker")
                .ExecuteAttachmentAsync(Generation, Arguments, AcknowledgementBudget, watchdog.Token);
        }

        internal async Task<TmuxCommandResult> SendAsync(TmuxCommandRequest request, CancellationToken token)
        {
            Requests.Add(request);
            IReadOnlyList<string> arguments = request.LogicalArguments;
            if (arguments.Contains("attach-session", StringComparer.Ordinal))
            {
                return await Attach(request, token);
            }
            int index = arguments.ToList().IndexOf("wait-for");
            Assert.True(index >= 0);
            Assert.True(request.PreventServerStart);
            if (arguments[index + 1] == "-S")
            {
                Withdrawals++;
                if (FailWithdrawal)
                {
                    throw new TmuxTransportException("withdrawal failed", arguments, TmuxDispatchState.Unknown);
                }
                Signal();
            }
            else
            {
                Channel = arguments[^1];
                if (RejectWaitGeneration)
                {
                    return Result(request, "42:101\n", 1, "unknown command: stale_marker\n");
                }
                try
                {
                    await signal.Task.WaitAsync(token);
                    if (CancelReceipt)
                    {
                        throw new OperationCanceledException(new CancellationToken(canceled: true));
                    }
                }
                finally
                {
                    WaiterFinished = true;
                    waiterCompleted.TrySetResult();
                }
            }
            return Result(request, "42:100\n");
        }
    }
}
