using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Transport;

internal static class UnixTestEnvironment
{
    public static bool IsUnix => !OperatingSystem.IsWindows();
}

internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Requires a Unix process environment.";
        SkipType = typeof(UnixTestEnvironment);
        SkipUnless = nameof(UnixTestEnvironment.IsUnix);
    }
}

internal sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Requires a Unix process environment.";
        SkipType = typeof(UnixTestEnvironment);
        SkipUnless = nameof(UnixTestEnvironment.IsUnix);
    }
}

[UnsupportedOSPlatform("windows")]
public sealed class TmuxProcessTransportTests
{
    [UnixFact]
    public async Task Preserves_raw_bytes_and_projects_universal_newlines()
    {
        byte[] stdout = [.. Encoding.UTF8.GetBytes("alpha\r\nbeta\rgamma\n")];
        byte[] stderr = [.. Encoding.UTF8.GetBytes("warning\rline\n")];
        var process = FakeProcessHandle.Completed(7042, stdout, stderr, exitCode: 0);
        var transport = CreateTransport(process);

        TmuxCommandResult result = await transport.ExecuteAsync(
            ["display-message", "-p", "#{session_name}"],
            TestContext.Current.CancellationToken);

        Assert.Equal(stdout, result.StandardOutput.ToArray());
        Assert.Equal(stderr, result.StandardError.ToArray());
        Assert.Equal(["alpha", "beta", "gamma"], result.StandardOutputLines);
        Assert.Equal(["warning", "line"], result.StandardErrorLines);
    }

    [UnixFact]
    public async Task Treats_public_semicolon_as_data_and_internal_typed_separator_as_structure()
    {
        var first = FakeProcessHandle.Completed(7043, [], [], exitCode: 0);
        var second = FakeProcessHandle.Completed(7044, [], [], exitCode: 0);
        var launcher = new QueueProcessLauncher(first, second);
        var transport = new TmuxProcessTransport("tmux", launcher: launcher);

        await transport.ExecuteAsync(
            ["display-message", "", "literal;", "already\\;"],
            TestContext.Current.CancellationToken);
        await transport.ExecuteAsync(
            TmuxCommandRequest.Group(
                ["display-message", "first"],
                ["display-message", "second"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["display-message", "", "literal\\;", "already\\\\;"],
            launcher.StartInfos[0].ArgumentList);
        Assert.Equal(
            ["display-message", "first", ";", "display-message", "second"],
            launcher.StartInfos[1].ArgumentList);
    }

    [UnixFact]
    public async Task Defensively_copies_logical_arguments_and_uses_deep_record_equality()
    {
        var arguments = new List<string> { "list-sessions" };
        var process = FakeProcessHandle.Completed(7045, "one\n"u8.ToArray(), [], exitCode: 0);
        var transport = CreateTransport(process);

        TmuxCommandResult result = await transport.ExecuteAsync(
            arguments,
            TestContext.Current.CancellationToken);
        arguments[0] = "kill-server";
        var equivalent = new TmuxCommandResult(
            ["list-sessions"],
            0,
            "one\n"u8.ToArray(),
            ReadOnlyMemory<byte>.Empty,
            ["one"],
            []);

        Assert.Equal(["list-sessions"], result.Arguments);
        Assert.False(result.Arguments is string[]);
        Assert.False(result.StandardOutputLines is string[]);
        Assert.Equal(equivalent, result);
        Assert.Equal(equivalent.GetHashCode(), result.GetHashCode());
    }

    [UnixFact]
    public void Raw_memory_access_cannot_mutate_result_value_semantics()
    {
        var result = new TmuxCommandResult(
            ["display-message"],
            0,
            new byte[] { 1, 2, 3 },
            new byte[] { 4, 5, 6 },
            ["stdout"],
            ["stderr"]);
        var equivalent = new TmuxCommandResult(
            ["display-message"],
            0,
            new byte[] { 1, 2, 3 },
            new byte[] { 4, 5, 6 },
            ["stdout"],
            ["stderr"]);
        int originalHashCode = result.GetHashCode();

        MemoryMarshal.AsMemory(result.StandardOutput).Span[0] = 99;
        MemoryMarshal.AsMemory(result.StandardError).Span[0] = 98;

        Assert.Equal(new byte[] { 1, 2, 3 }, result.StandardOutput.ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, result.StandardError.ToArray());
        Assert.Equal(equivalent, result);
        Assert.Equal(originalHashCode, result.GetHashCode());
    }

    [UnixFact]
    public async Task Enforces_transport_limits_and_bounded_cleanup()
    {
        var process = FakeProcessHandle.Running(
            7046,
            standardOutput: [1, 2, 3, 4, 5],
            standardError: []);
        var limits = new TmuxTransportLimits(
            MaxArguments: 1,
            MaxCapturedBytesPerStream: 4,
            CleanupTimeoutValue: TimeSpan.FromSeconds(1));
        var transport = new TmuxProcessTransport("tmux", limits: limits, launcher: new QueueProcessLauncher(process));

        TmuxTransportException argumentError = await Assert.ThrowsAsync<TmuxTransportException>(
            () => transport.ExecuteAsync(
                ["display-message", "too-many"],
                TestContext.Current.CancellationToken));

        Assert.False(process.WasKilled);
        Assert.Equal(["display-message", "too-many"], argumentError.Arguments);

        TmuxTransportException streamError = await Assert.ThrowsAsync<TmuxTransportException>(
            () => transport.ExecuteAsync(
                ["display-message"],
                TestContext.Current.CancellationToken));

        Assert.True(process.WasKilled);
        Assert.IsType<InvalidDataException>(streamError.InnerException);
    }

    [UnixFact]
    public void ThrowIfFailed_observes_projected_stderr_without_mutating_raw_bytes()
    {
        byte[] stderr = "warning\n"u8.ToArray();
        var result = new TmuxCommandResult(
            ["display-message"],
            0,
            ReadOnlyMemory<byte>.Empty,
            stderr,
            [],
            ["warning"]);

        TmuxCommandException error = Assert.Throws<TmuxCommandException>(
            () => TmuxCommandFailure.ThrowIfFailed(result, "display message"));

        Assert.Same(result, error.Result);
        Assert.Equal(stderr, result.StandardError.ToArray());
    }

    [UnixFact]
    public async Task Missing_binary_throws_TmuxCommandNotFoundException_with_configured_path()
    {
        const string MissingPath = "missing-libtmux-tmux";
        var transport = new TmuxProcessTransport(
            MissingPath,
            launcher: new ThrowingProcessLauncher(new Win32Exception(2, "missing")));

        TmuxCommandNotFoundException error =
            await Assert.ThrowsAsync<TmuxCommandNotFoundException>(
                () => transport.ExecuteAsync(
                    ["list-sessions"],
                    TestContext.Current.CancellationToken));

        Assert.Equal(MissingPath, error.TmuxBinaryPath);
    }

    [UnixFact]
    public async Task Pre_start_cancellation_throws_OperationCanceledException_with_caller_token_without_starting_process()
    {
        var launcher = new QueueProcessLauncher(
            FakeProcessHandle.Completed(7047, [], [], exitCode: 0));
        var transport = new TmuxProcessTransport("tmux", launcher: launcher);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.ExecuteAsync(["list-sessions"], cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(launcher.StartInfos);
    }

    [UnixFact]
    public async Task Cancellation_during_async_preflight_prevents_process_start()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var launcher = new QueueProcessLauncher(
            FakeProcessHandle.Completed(7057, [], [], exitCode: 0));
        var transport = new TmuxProcessTransport(
            "tmux",
            launcher: launcher,
            beforeStart: async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        Task<TmuxCommandResult> execution = transport.ExecuteAsync(
            ["list-sessions"],
            cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution);

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(launcher.StartInfos);
    }

    [UnixFact]
    public async Task Cancellation_during_argv_setup_still_prevents_process_start()
    {
        using var cancellation = new CancellationTokenSource();
        var launcher = new QueueProcessLauncher(
            FakeProcessHandle.Completed(7056, [], [], exitCode: 0));
        var transport = new TmuxProcessTransport("tmux", launcher: launcher);
        var arguments = new CancelingArguments(
            ["display-message"],
            cancellation);

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.ExecuteAsync(arguments, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(launcher.StartInfos);
    }

    [UnixFact]
    public async Task Post_start_cancellation_throws_TmuxOperationCanceledException_with_true_execution_risk_and_client_pid()
    {
        var process = FakeProcessHandle.Running(7048, [], []);
        var transport = CreateTransport(process);
        using var cancellation = new CancellationTokenSource();
        Task<TmuxCommandResult> execution = transport.ExecuteAsync(
            ["wait-for", "blocked"],
            cancellation.Token);
        await process.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        TmuxOperationCanceledException error =
            await Assert.ThrowsAsync<TmuxOperationCanceledException>(() => execution);

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(error.CommandMayHaveExecuted);
        Assert.Equal(7048, error.ClientProcessId);
        Assert.True(process.WasKilled);
    }

    [UnixFact]
    public async Task Cancellation_stops_owned_output_reads_after_the_client_has_exited()
    {
        var heldOutput = new BlockingReadStream();
        var process = FakeProcessHandle.CompletedWithStreams(
            7068, heldOutput, new MemoryStream([], writable: false), exitCode: 0);
        var transport = CreateTransport(process);
        using var cancellation = new CancellationTokenSource();
        Task<TmuxCommandResult> execution = transport.ExecuteAsync(["wait-for", "held-output"], cancellation.Token);
        await process.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await cancellation.CancelAsync();
            TmuxOperationCanceledException failure = await Assert.ThrowsAsync<TmuxOperationCanceledException>(() =>
                execution.WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));

            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.True(process.HasExited);
        }
        finally
        {
            heldOutput.Complete();
            await IgnoreFailureAsync(execution);
        }
    }

    [UnixFact]
    public async Task Cleanup_failure_throws_TmuxCleanupException_with_original_context()
    {
        var cleanupFailure = new IOException("cleanup fault");
        var process = FakeProcessHandle.RunningWithStreams(
            7049,
            new MemoryStream([], writable: false),
            new MemoryStream([], writable: false),
            killFailure: cleanupFailure,
            exitWhenKillThrows: true);
        var transport = CreateTransport(process);
        using var cancellation = new CancellationTokenSource();
        Task<TmuxCommandResult> execution = transport.ExecuteAsync(
            ["wait-for", "blocked"],
            cancellation.Token);
        await process.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        TmuxCleanupException error = await Assert.ThrowsAsync<TmuxCleanupException>(() => execution);

        Assert.Equal(cancellation.Token, error.OriginalCancellation.CancellationToken);
        Assert.Equal(7049, error.ClientProcessId);
        Assert.Same(cleanupFailure, error.CleanupFailure);
    }

    [UnixFact]
    public async Task Cleanup_treats_exit_between_probe_and_kill_as_benign()
    {
        var process = FakeProcessHandle.Running(
            7059,
            [],
            [],
            exitBeforeKill: true);
        var transport = CreateTransport(process);
        using var cancellation = new CancellationTokenSource();
        Task<TmuxCommandResult> execution = transport.ExecuteAsync(
            ["wait-for", "blocked"],
            cancellation.Token);
        await process.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        TmuxOperationCanceledException error =
            await Assert.ThrowsAsync<TmuxOperationCanceledException>(() => execution);

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(process.WasKilled);
    }

    [UnixFact]
    public async Task Injects_launcher_clock_and_limits_without_wall_clock_sleeps()
    {
        var heldOutput = new BlockingReadStream(ignoreCancellation: true);
        var process = FakeProcessHandle.CompletedWithStreams(
            7051,
            heldOutput,
            new MemoryStream([], writable: false),
            exitCode: 0);
        var timeProvider = new ImmediateTimeoutTimeProvider();
        var transport = new TmuxProcessTransport(
            "tmux",
            new QueueProcessLauncher(process),
            timeProvider: timeProvider);
        using var cancellation = new CancellationTokenSource();
        Task<TmuxCommandResult> execution = transport.ExecuteAsync(
            ["display-message"],
            cancellation.Token);
        await process.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        try
        {
            cancellation.Cancel();
            TmuxCleanupException error = await Assert.ThrowsAsync<TmuxCleanupException>(
                () => execution.WaitAsync(
                    TimeSpan.FromSeconds(1),
                    TestContext.Current.CancellationToken));

            Assert.IsType<TimeoutException>(error.CleanupFailure);
            Assert.Equal(cancellation.Token, error.OriginalCancellation.CancellationToken);
            Assert.True(timeProvider.WasTimerCreated);
        }
        finally
        {
            heldOutput.Complete();
            await IgnoreFailureAsync(execution);
        }
    }

    [UnixFact]
    public async Task Cleanup_preserves_an_injected_clock_failure_while_work_is_pending()
    {
        var heldOutput = new BlockingReadStream(ignoreCancellation: true);
        var process = FakeProcessHandle.CompletedWithStreams(
            7062,
            heldOutput,
            new MemoryStream([], writable: false),
            exitCode: 0);
        var clockFailure = new InvalidOperationException("clock failed");
        var transport = new TmuxProcessTransport(
            "tmux",
            new QueueProcessLauncher(process),
            timeProvider: new ThrowingTimeProvider(clockFailure));
        using var cancellation = new CancellationTokenSource();
        Task<TmuxCommandResult> execution = transport.ExecuteAsync(
            ["display-message"],
            cancellation.Token);
        await process.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        try
        {
            cancellation.Cancel();
            TmuxCleanupException error = await Assert.ThrowsAsync<TmuxCleanupException>(
                () => execution.WaitAsync(
                    TimeSpan.FromSeconds(1),
                    TestContext.Current.CancellationToken));

            Assert.Same(clockFailure, error.CleanupFailure);
        }
        finally
        {
            heldOutput.Complete();
            await IgnoreFailureAsync(execution);
        }
    }

    [UnixFact]
    public async Task Caller_cancellation_wins_at_the_final_completion_boundary()
    {
        using var cancellation = new CancellationTokenSource();
        var process = FakeProcessHandle.CompletedWithStreams(
            7052,
            new CancelingReadStream(cancellation),
            new MemoryStream([], writable: false),
            exitCode: 0);
        var transport = CreateTransport(process);

        TmuxOperationCanceledException error =
            await Assert.ThrowsAsync<TmuxOperationCanceledException>(
                () => transport.ExecuteAsync(["display-message"], cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(error.CommandMayHaveExecuted);
        Assert.Equal(7052, error.ClientProcessId);
    }

    [UnixFact]
    public async Task Cancellation_during_result_materialization_uses_the_post_start_path()
    {
        using var cancellation = new CancellationTokenSource();
        var process = FakeProcessHandle.Completed(
            7064,
            "ready\n"u8.ToArray(),
            [],
            exitCode: 0,
            onExitCode: cancellation.Cancel);
        var transport = CreateTransport(process);

        TmuxOperationCanceledException error =
            await Assert.ThrowsAsync<TmuxOperationCanceledException>(
                () => transport.ExecuteAsync(["display-message"], cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(error.CommandMayHaveExecuted);
        Assert.Equal(7064, error.ClientProcessId);
    }

    [UnixFact]
    public async Task Exit_code_materialization_failure_uses_typed_transport_cleanup()
    {
        var materializationFailure = new IOException("exit code unavailable");
        var process = FakeProcessHandle.Completed(
            7065,
            [],
            [],
            exitCode: 0,
            exitCodeFailure: materializationFailure);
        var transport = CreateTransport(process);

        TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(
            () => transport.ExecuteAsync(
                ["display-message"],
                TestContext.Current.CancellationToken));

        Assert.Same(materializationFailure, error.InnerException);
    }

    [UnixFact]
    public async Task Post_start_task_acquisition_failure_still_kills_and_reaps_client()
    {
        var acquisitionFailure = new IOException("stdout acquisition failed");
        var process = FakeProcessHandle.Running(
            7053,
            [],
            [],
            standardOutputFailure: acquisitionFailure);
        var transport = CreateTransport(process);

        TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(
            () => transport.ExecuteAsync(
                ["display-message"],
                TestContext.Current.CancellationToken));

        Assert.True(process.WasKilled);
        Assert.True(process.WaitCallCount >= 2);
        Assert.Contains(acquisitionFailure.Message, FlattenMessages(error));
    }

    [UnixFact]
    public async Task Cleanup_continues_after_kill_failure_and_preserves_concurrent_pump_failure()
    {
        var firstPumpFailure = new IOException("stdout pump failed");
        var secondPumpFailure = new IOException("stderr pump failed");
        var killFailure = new IOException("kill failed");
        var process = FakeProcessHandle.RunningWithStreams(
            7054,
            new ThrowingReadStream(firstPumpFailure),
            new ThrowingReadStream(secondPumpFailure),
            killFailure: killFailure,
            exitWhenKillThrows: true);
        var transport = CreateTransport(process);

        TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(
            () => transport.ExecuteAsync(
                ["display-message"],
                TestContext.Current.CancellationToken));
        string messages = FlattenMessages(error);

        Assert.True(process.WasKilled);
        Assert.True(process.WaitCallCount >= 2);
        Assert.Contains(firstPumpFailure.Message, messages);
        Assert.Contains(secondPumpFailure.Message, messages);
        Assert.Contains(killFailure.Message, messages);
    }

    [UnixFact]
    public async Task Cleanup_does_not_duplicate_the_primary_aggregate_exception_graph()
    {
        var firstFailure = new IOException("first pump failure");
        var secondFailure = new InvalidDataException("second pump failure");
        var primaryFailure = new AggregateException(firstFailure, secondFailure);
        var process = FakeProcessHandle.RunningWithStreams(
            7057,
            new ThrowingReadStream(primaryFailure),
            new MemoryStream([], writable: false));
        var transport = CreateTransport(process);

        TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(
            () => transport.ExecuteAsync(
                ["display-message"],
                TestContext.Current.CancellationToken));
        IReadOnlyList<Exception> leaves = FlattenLeaves(error);

        Assert.Single(leaves, failure => ReferenceEquals(failure, firstFailure));
        Assert.Single(leaves, failure => ReferenceEquals(failure, secondFailure));
    }

    [UnixFact]
    public async Task Cleanup_preserves_a_secondary_canceled_pump()
    {
        var primaryFailure = new IOException("primary pump failure");
        using var pumpCancellation = new CancellationTokenSource();
        pumpCancellation.Cancel();
        var process = FakeProcessHandle.RunningWithStreams(
            7058,
            new ThrowingReadStream(primaryFailure),
            new CanceledReadStream(pumpCancellation.Token));
        var transport = CreateTransport(process);

        TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(
            () => transport.ExecuteAsync(
                ["display-message"],
                TestContext.Current.CancellationToken));
        IReadOnlyList<Exception> leaves = FlattenLeaves(error);

        Assert.Contains(leaves, failure => ReferenceEquals(failure, primaryFailure));
        Assert.Contains(leaves, static failure => failure is TaskCanceledException);
    }

    [UnixFact]
    public async Task Cleanup_matches_the_primary_canceled_task_by_identity()
    {
        using var secondaryCancellation = new CancellationTokenSource();
        using var primaryCancellation = new CancellationTokenSource();
        primaryCancellation.Cancel();
        var secondaryOutput = new ControllableCanceledReadStream();
        var process = FakeProcessHandle.RunningWithStreams(
            7060,
            secondaryOutput,
            new CanceledReadStream(primaryCancellation.Token),
            onKill: () => secondaryOutput.Cancel(secondaryCancellation.Token));
        var transport = CreateTransport(process);

        TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(
            () => transport.ExecuteAsync(
                ["display-message"],
                TestContext.Current.CancellationToken));
        TaskCanceledException[] cancellations =
        [.. FlattenLeaves(error).OfType<TaskCanceledException>()];

        Assert.Equal(2, cancellations.Length);
        Assert.Equal(
            2,
            cancellations
                .Select(static failure => failure.Task)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());
        Assert.Single(
            cancellations,
            failure => failure.CancellationToken == primaryCancellation.Token);
        Assert.Single(
            cancellations,
            failure => failure.CancellationToken == secondaryCancellation.Token);
    }

    [UnixFact]
    public async Task Cleanup_deduplicates_a_shared_secondary_failure_instance()
    {
        var primaryFailure = new IOException("primary pump failure");
        var sharedFailure = new IOException("shared cleanup failure");
        var process = FakeProcessHandle.RunningWithStreams(
            7061,
            new ThrowingReadStream(primaryFailure),
            new ThrowingReadStream(sharedFailure),
            killFailure: sharedFailure,
            exitWhenKillThrows: true);
        var transport = CreateTransport(process);

        TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(
            () => transport.ExecuteAsync(
                ["display-message"],
                TestContext.Current.CancellationToken));
        IReadOnlyList<Exception> leaves = FlattenLeaves(error);

        Assert.Single(leaves, failure => ReferenceEquals(failure, sharedFailure));
    }

    [UnixFact]
    public async Task Cleanup_does_not_misclassify_an_operation_timeout_as_the_budget()
    {
        var primaryFailure = new IOException("primary pump failure");
        var operationTimeout = new TimeoutException("kill operation timed out");
        var process = FakeProcessHandle.RunningWithStreams(
            7063,
            new ThrowingReadStream(primaryFailure),
            new MemoryStream([], writable: false),
            killFailure: operationTimeout,
            exitWhenKillThrows: true);
        var transport = CreateTransport(process);

        TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(
            () => transport.ExecuteAsync(
                ["display-message"],
                TestContext.Current.CancellationToken));
        IReadOnlyList<Exception> leaves = FlattenLeaves(error);

        Assert.Single(leaves.OfType<TimeoutException>());
        Assert.Single(leaves, failure => ReferenceEquals(failure, operationTimeout));
    }

    [UnixFact]
    public async Task Cleanup_timeout_includes_a_blocking_kill_attempt()
    {
        using var killGate = new ManualResetEventSlim(initialState: false);
        var pumpFailure = new IOException("pump failed before cleanup");
        var failingOutput = new ControllableThrowingReadStream();
        var process = FakeProcessHandle.RunningWithStreams(
            7055,
            failingOutput,
            new MemoryStream([], writable: false),
            killGate: killGate);
        var limits = new TmuxTransportLimits(
            CleanupTimeoutValue: TimeSpan.FromMilliseconds(30));
        var transport = new TmuxProcessTransport(
            "tmux",
            new QueueProcessLauncher(process),
            limits: limits);
        Task<TmuxCommandResult> execution = transport.ExecuteAsync(
            ["display-message"],
            TestContext.Current.CancellationToken);
        await failingOutput.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        failingOutput.Fail(pumpFailure);

        try
        {
            TmuxTransportException error = await Assert.ThrowsAsync<TmuxTransportException>(
                () => execution.WaitAsync(
                    TimeSpan.FromSeconds(1),
                    TestContext.Current.CancellationToken));

            Assert.Contains(nameof(TimeoutException), FlattenExceptionTypes(error));
        }
        finally
        {
            killGate.Set();
        }
    }

    [UnixFact]
    public void Defaults_freeze_transport_resource_budgets()
    {
        var limits = new TmuxTransportLimits();

        Assert.Equal(4096, limits.MaxArguments);
        Assert.Equal(64 * 1024 * 1024, limits.MaxCapturedBytesPerStream);
        Assert.Equal(TimeSpan.FromSeconds(5), limits.CleanupTimeout);
    }

    [UnixFact]
    public async Task Invalid_utf8_projects_each_bad_byte_as_lowercase_hex_escape()
    {
        byte[] stdout = [0x66, 0x80, 0xff, 0x0a];
        var process = FakeProcessHandle.Completed(7050, stdout, [], exitCode: 0);
        var transport = CreateTransport(process);

        TmuxCommandResult result = await transport.ExecuteAsync(
            ["display-message"],
            TestContext.Current.CancellationToken);

        Assert.Equal(stdout, result.StandardOutput.ToArray());
        Assert.Equal(["f\\x80\\xff"], result.StandardOutputLines);
    }

    [UnixFact]
    public async Task Command_fragments_inject_stable_targets_and_allow_overrides()
    {
        var invocations = new List<IReadOnlyList<string>>();
        var dispatcher = new TmuxCommandDispatcher((arguments, _) =>
        {
            invocations.Add([.. arguments]);
            return Task.FromResult(EmptyResult(arguments));
        });
        var session = new Session(dispatcher, "$7");
        var window = new Window(dispatcher, "@8");
        var pane = new Pane(dispatcher, "%9");

        await session.ExecuteCommandAsync(
            ["rename-session", "next"],
            cancellationToken: TestContext.Current.CancellationToken);
        await window.ExecuteCommandAsync(
            ["rename-window", "next"],
            "@12",
            TestContext.Current.CancellationToken);
        await pane.ExecuteCommandAsync(
            ["send-keys", "hello"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["rename-session", "-t", "$7", "next"], invocations[0]);
        Assert.Equal(["rename-window", "-t", "@12", "next"], invocations[1]);
        Assert.Equal(["send-keys", "-t", "%9", "hello"], invocations[2]);
    }

    [UnixTheory]
    [InlineData("-t")]
    [InlineData("-t$99")]
    [InlineData("-pt$99")]
    [InlineData("-2t%99")]
    public async Task Command_fragments_reject_raw_target_flags_before_dispatch(string targetFlag)
    {
        var invocations = new List<IReadOnlyList<string>>();
        var dispatcher = new TmuxCommandDispatcher((arguments, _) =>
        {
            invocations.Add([.. arguments]);
            return Task.FromResult(EmptyResult(arguments));
        });
        var session = new Session(dispatcher, "$7");

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.ExecuteCommandAsync(
                ["display-message", targetFlag, "other"],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(invocations);
    }

    private static TmuxProcessTransport CreateTransport(FakeProcessHandle process) =>
        new("tmux", launcher: new QueueProcessLauncher(process));

    private static TmuxCommandResult EmptyResult(IReadOnlyList<string> arguments) =>
        new(arguments, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, [], []);

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }

    private static string FlattenMessages(Exception error) =>
        string.Join(
            "\n",
            error is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.Select(FlattenMessages)
                : [error.Message, error.InnerException is null ? "" : FlattenMessages(error.InnerException)]);

    private static string FlattenExceptionTypes(Exception error) =>
        string.Join(
            "\n",
            error is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.Select(FlattenExceptionTypes)
                : [error.GetType().Name, error.InnerException is null ? "" : FlattenExceptionTypes(error.InnerException)]);

    private static IReadOnlyList<Exception> FlattenLeaves(Exception error)
    {
        if (error is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions;
        }

        if (error.InnerException is null)
        {
            return [error];
        }

        return FlattenLeaves(error.InnerException);
    }

    private sealed class QueueProcessLauncher(params FakeProcessHandle[] processes)
        : ITmuxProcessLauncher
    {
        private readonly Queue<FakeProcessHandle> _processes = new(processes);

        public List<ProcessStartInfo> StartInfos { get; } = [];

        public ITmuxProcessHandle Start(ProcessStartInfo startInfo)
        {
            StartInfos.Add(startInfo);
            return _processes.Dequeue();
        }
    }

    private sealed class ThrowingProcessLauncher(Exception failure) : ITmuxProcessLauncher
    {
        public ITmuxProcessHandle Start(ProcessStartInfo startInfo) => throw failure;
    }

    private sealed class CancelingArguments(
        IReadOnlyList<string> values,
        CancellationTokenSource cancellation) : IReadOnlyList<string>
    {
        public int Count => values.Count;

        public string this[int index] => values[index];

        public IEnumerator<string> GetEnumerator()
        {
            foreach (string value in values)
            {
                yield return value;
            }

            cancellation.Cancel();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class FakeProcessHandle : ITmuxProcessHandle
    {
        private readonly TaskCompletionSource _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Exception? _killFailure;
        private readonly ManualResetEventSlim? _killGate;
        private readonly bool _exitWhenKillThrows;
        private readonly bool _exitBeforeKill;
        private readonly Action? _onKill;
        private readonly Action? _onExitCode;
        private readonly Exception? _exitCodeFailure;
        private readonly Exception? _standardOutputFailure;
        private readonly int _exitCode;
        private readonly Stream _standardError;
        private readonly Stream _standardOutput;

        private FakeProcessHandle(
            int id,
            Stream standardOutput,
            Stream standardError,
            int exitCode,
            bool completed,
            Exception? killFailure,
            Exception? standardOutputFailure = null,
            ManualResetEventSlim? killGate = null,
            bool exitWhenKillThrows = false,
            bool exitBeforeKill = false,
            Action? onKill = null,
            Action? onExitCode = null,
            Exception? exitCodeFailure = null)
        {
            Id = id;
            _standardOutput = standardOutput;
            _standardError = standardError;
            _exitCode = exitCode;
            _killFailure = killFailure;
            _standardOutputFailure = standardOutputFailure;
            _killGate = killGate;
            _exitWhenKillThrows = exitWhenKillThrows;
            _exitBeforeKill = exitBeforeKill;
            _onKill = onKill;
            _onExitCode = onExitCode;
            _exitCodeFailure = exitCodeFailure;
            if (completed)
            {
                _exit.SetResult();
            }
        }

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id { get; }

        public Stream StandardOutput => _standardOutputFailure is null
            ? _standardOutput
            : throw _standardOutputFailure;

        public Stream StandardError => _standardError;

        public int ExitCode
        {
            get
            {
                _onExitCode?.Invoke();
                return _exitCodeFailure is null ? _exitCode : throw _exitCodeFailure;
            }
        }

        public bool HasExited
        {
            get
            {
                if (_exitBeforeKill && !_exit.Task.IsCompleted)
                {
                    _exit.TrySetResult();
                    return false;
                }

                return _exit.Task.IsCompletedSuccessfully;
            }
        }

        public bool WasKilled { get; private set; }

        public int WaitCallCount { get; private set; }

        internal static FakeProcessHandle Completed(
            int id,
            byte[] standardOutput,
            byte[] standardError,
            int exitCode,
            Action? onExitCode = null,
            Exception? exitCodeFailure = null) =>
            new(
                id,
                new MemoryStream(standardOutput, writable: false),
                new MemoryStream(standardError, writable: false),
                exitCode,
                completed: true,
                killFailure: null,
                onExitCode: onExitCode,
                exitCodeFailure: exitCodeFailure);

        internal static FakeProcessHandle CompletedWithStreams(
            int id,
            Stream standardOutput,
            Stream standardError,
            int exitCode) =>
            new(id, standardOutput, standardError, exitCode, completed: true, killFailure: null);

        internal static FakeProcessHandle Running(
            int id,
            byte[] standardOutput,
            byte[] standardError,
            Exception? killFailure = null,
            Exception? standardOutputFailure = null,
            bool exitBeforeKill = false) =>
            new(
                id,
                new MemoryStream(standardOutput, writable: false),
                new MemoryStream(standardError, writable: false),
                0,
                completed: false,
                killFailure,
                standardOutputFailure,
                exitBeforeKill: exitBeforeKill);

        internal static FakeProcessHandle RunningWithStreams(
            int id,
            Stream standardOutput,
            Stream standardError,
            Exception? killFailure = null,
            ManualResetEventSlim? killGate = null,
            bool exitWhenKillThrows = false,
            Action? onKill = null) =>
            new(
                id,
                standardOutput,
                standardError,
                0,
                completed: false,
                killFailure,
                killGate: killGate,
                exitWhenKillThrows: exitWhenKillThrows,
                onKill: onKill);

        public void Kill()
        {
            WasKilled = true;
            _onKill?.Invoke();
            _killGate?.Wait();
            if (_exitBeforeKill)
            {
                throw new InvalidOperationException("The process has already exited.");
            }

            if (_killFailure is not null)
            {
                if (_exitWhenKillThrows)
                {
                    _exit.TrySetResult();
                }

                throw _killFailure;
            }

            _exit.TrySetResult();
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            WaitCallCount++;
            Started.TrySetResult();
            await _exit.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _standardOutput.Dispose();
            _standardError.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingReadStream(bool ignoreCancellation = false) : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void Complete() => _read.TrySetResult(0);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(_read.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : cancellationToken));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ControllableThrowingReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void Fail(Exception failure) => _read.TrySetException(failure);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            return new ValueTask<int>(_read.Task.WaitAsync(cancellationToken));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ControllableCanceledReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void Cancel(CancellationToken cancellationToken) =>
            _read.TrySetCanceled(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(_read.Task);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class CancelingReadStream(CancellationTokenSource cancellation) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(0);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class CanceledReadStream(CancellationToken cancellationToken) : Stream
    {
        private readonly CancellationToken _cancellationToken = cancellationToken;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromCanceled<int>(_cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingReadStream(Exception failure) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw failure;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ImmediateTimeoutTimeProvider : TimeProvider
    {
        public bool WasTimerCreated { get; private set; }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            WasTimerCreated = true;
            return new ImmediateTimer(callback, state);
        }

        private sealed class ImmediateTimer : ITimer
        {
            private readonly Task _callback;

            internal ImmediateTimer(TimerCallback callback, object? state) =>
                _callback = Task.Run(() => callback(state));

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public async ValueTask DisposeAsync() => await _callback.ConfigureAwait(false);
        }
    }

    private sealed class ThrowingTimeProvider(Exception failure) : TimeProvider
    {
        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => throw failure;
    }
}

public sealed class TransportPlatformContractTests
{
    [Fact]
    public void Unix_process_tests_have_runtime_skip_metadata()
    {
        Type testType = typeof(TmuxProcessTransportTests);
        PropertyInfo? condition = typeof(UnixTestEnvironment).GetProperty(
            nameof(UnixTestEnvironment.IsUnix),
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(condition);
        Assert.Equal(typeof(bool), condition.PropertyType);
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

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(
        Skip = "Requires Windows.",
        SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    [SuppressMessage(
        "Interoperability",
        "CA1416:Validate platform compatibility",
        Justification = "This Windows-only test verifies process launch is reachable.")]
    public async Task Process_transport_reaches_process_launch_on_windows()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-tmux-{Guid.NewGuid():N}.exe");
        var transport = new TmuxProcessTransport(missing);

        await Assert.ThrowsAsync<TmuxCommandNotFoundException>(
            () => transport.ExecuteAsync(
                ["display-message"],
                TestContext.Current.CancellationToken));
    }
}
