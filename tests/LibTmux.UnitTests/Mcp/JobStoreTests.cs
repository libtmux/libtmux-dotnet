using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using LibTmux.Internal;
using LibTmux.Mcp;
using LibTmux.UnitTests.Connection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace LibTmux.UnitTests.Mcp;

[UnsupportedOSPlatform("windows")]
public sealed class JobStoreTests
{
    [Fact]
    public async Task Start_rejects_a_pane_from_another_endpoint_or_generation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ServerGeneration generation = new(51, 501);
        FakeEndpoint origin = new("owner-a", generation);
        FakeEndpoint otherEndpoint = new("owner-b", generation);
        FakeEndpoint restarted = new("owner-a", new ServerGeneration(52, 502));
        await using JobStore jobs = new();

        Exception? endpointError = Record.Exception(() =>
        {
            _ = jobs.StartAsync(
                origin.Server,
                otherEndpoint.Pane,
                "echo wrong-endpoint",
                suppressHistory: true,
                token);
        });
        Exception? generationError = Record.Exception(() =>
        {
            _ = jobs.StartAsync(
                restarted.Server,
                origin.Pane,
                "echo stale-generation",
                suppressHistory: true,
                token);
        });
        McpException endpointMismatch = Assert.IsType<McpException>(endpointError);
        McpException generationMismatch = Assert.IsType<McpException>(generationError);

        Assert.Contains("different tmux endpoint", endpointMismatch.Message, StringComparison.Ordinal);
        Assert.Contains("server generation", generationMismatch.Message, StringComparison.Ordinal);
        Assert.Empty(origin.Commands);
        Assert.Empty(otherEndpoint.Commands);
        Assert.Empty(restarted.Commands);
        Assert.Equal(0, jobs.List().TotalJobs);
    }

    [Fact]
    public async Task Start_rejects_an_unreturnable_handle_before_dispatch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new(
            new string('s', 6_000),
            new ServerGeneration(61, 601));
        await using JobStore jobs = new();

        Exception? failure = Record.Exception(() =>
        {
            _ = jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                "echo never-dispatched",
                suppressHistory: true,
                maxCommandBytes: 4_000,
                cancellationToken: token);
        });
        McpException tooLarge = Assert.IsType<McpException>(failure);

        Assert.Contains("job handle response", tooLarge.Message, StringComparison.Ordinal);
        Assert.Contains(ServerPolicy.MaxBytesVariable, tooLarge.Message, StringComparison.Ordinal);
        Assert.Empty(endpoint.Commands);
        Assert.Equal(0, jobs.List().TotalJobs);
    }

    [Fact]
    public async Task Same_pane_id_on_two_servers_stays_bound_to_the_starting_endpoint()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint origin = new("jobs-a", new ServerGeneration(101, 1001));
        FakeEndpoint other = new("jobs-b", new ServerGeneration(202, 2002));
        await using JobStore jobs = new();
        await using PaneActivityHub activity = new();
        using TmuxConnectionAccessor connections = new(other.Server);
        var write = new WriteTools(connections, new ServerPolicy(), activity, jobs);
        const string Secret = "TOKEN=do-not-return-this echo work";

        Assert.Equal(origin.Pane.Id, other.Pane.Id);
        JobInfo started = await jobs.StartAsync(
            origin.Server,
            origin.Pane,
            Secret,
            suppressHistory: true,
            token);

        Assert.Equal("jobs-a", started.SocketName);
        Assert.Null(started.SocketPath);
        Assert.NotEqual(
            other.Pane.Server.Connection?.GetEndpointFingerprint(),
            started.EndpointFingerprint);
        Assert.Equal(
            origin.Pane.Server.Connection?.GetEndpointFingerprint(),
            started.EndpointFingerprint);
        Assert.Equal(origin.Generation, started.ServerGeneration);
        Assert.Equal(Encoding.UTF8.GetByteCount(Secret), started.CommandBytes);
        Assert.True(
            Utf8JsonBudget.GetStructuredToolResultByteCount(started, ToolJson.Options) <= 4_000);
        Assert.DoesNotContain(
            Secret,
            JsonSerializer.Serialize(started, ToolJson.Options),
            StringComparison.Ordinal);

        McpException readMismatch = await Assert.ThrowsAsync<McpException>(
            () => write.JobAsync(started.JobId, socketName: "jobs-b", cancellationToken: token));
        McpException cancelMismatch = await Assert.ThrowsAsync<McpException>(
            () => write.CancelJobAsync(started.JobId, "jobs-b", token));

        Assert.Contains("jobs-a", readMismatch.Message, StringComparison.Ordinal);
        Assert.Contains("jobs-b", readMismatch.Message, StringComparison.Ordinal);
        Assert.Contains("jobs-a", cancelMismatch.Message, StringComparison.Ordinal);
        Assert.Empty(other.Commands);

        JobInfo cancelled = await write.CancelJobAsync(
            started.JobId,
            cancellationToken: token);
        Assert.Equal(JobState.Cancelled, cancelled.State);
        Assert.True(
            Utf8JsonBudget.GetStructuredToolResultByteCount(cancelled, ToolJson.Options) <= 4_000);
        Assert.Contains(
            origin.Commands,
            arguments => arguments.Contains("C-c", StringComparer.Ordinal));
        Assert.Empty(other.Commands);
    }

    [Fact]
    public async Task Concurrent_terminal_transitions_publish_one_complete_snapshot()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("atomic", new ServerGeneration(303, 3003));
        JobStore.StoredJob job = endpoint.Job("atomic-job");
        using var start = new ManualResetEventSlim(false);

        Task<bool> exited = Task.Run(
            () =>
            {
                start.Wait(token);
                return job.TryFinish(JobState.Exited, 7);
            },
            token);
        Task<bool> cancelled = Task.Run(
            () =>
            {
                start.Wait(token);
                return job.TryFinish(JobState.Cancelled, null);
            },
            token);

        start.Set();
        bool[] transitions = await Task.WhenAll(exited, cancelled);
        JobInfo snapshot = job.Describe();

        Assert.Single(transitions, won => won);
        Assert.NotNull(snapshot.EndedAt);
        if (snapshot.State == JobState.Exited)
        {
            Assert.Equal(7, snapshot.ExitStatus);
        }
        else
        {
            Assert.Equal(JobState.Cancelled, snapshot.State);
            Assert.Null(snapshot.ExitStatus);
        }
    }

    [Fact]
    public async Task Concurrent_output_collections_serialize_cursor_read_and_advance()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("cursor", new ServerGeneration(404, 4004));
        JobStore.StoredJob job = endpoint.Job("cursor-job");
        JobStore.StoredJob.OutputLease first = await job.AcquireOutputAsync(token);
        Task<JobStore.StoredJob.OutputLease> secondTask = job
            .AcquireOutputAsync(token)
            .AsTask();

        Assert.False(secondTask.IsCompleted);
        first.Advance("cursor-one");
        first.Dispose();

        using JobStore.StoredJob.OutputLease second = await secondTask;
        Assert.Equal("cursor-one", second.Cursor);
        second.Advance("cursor-two");
    }

    [Fact]
    public async Task Not_dispatched_start_failure_removes_the_unissued_handle()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("failed", new ServerGeneration(505, 5005));
        endpoint.Handler = (arguments, _) =>
            arguments.Contains("send-keys", StringComparer.Ordinal)
                ? Task.FromException<TmuxCommandResult>(new TmuxTransportException(
                    "The tmux client was not started.",
                    arguments,
                    TmuxDispatchState.NotDispatched))
                : Task.FromResult(endpoint.Success(arguments));
        await using JobStore jobs = new();

        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(
            () => jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                "echo never-started",
                suppressHistory: true,
                token));

        Assert.Equal(TmuxDispatchState.NotDispatched, failure.Dispatch);
        Assert.False(failure.Data.Contains(JobStore.RecoveryJobIdDataKey));
        JobList remembered = jobs.List();
        Assert.Equal(0, remembered.TotalJobs);
        Assert.Empty(remembered.Jobs);
    }

    [Fact]
    public async Task Not_dispatched_enter_after_payload_retains_a_recovery_handle()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("enter-failed", new ServerGeneration(525, 5205));
        int sendStage = 0;
        string? jobId = null;
        endpoint.Handler = async (arguments, cancellationToken) =>
        {
            if (arguments.Contains("send-keys", StringComparer.Ordinal))
            {
                int stage = Interlocked.Increment(ref sendStage);
                if (stage == 1)
                {
                    jobId = ExtractRunId(arguments);
                    return endpoint.Success(arguments);
                }

                throw new TmuxTransportException(
                    "Enter was not dispatched.",
                    arguments,
                    TmuxDispatchState.NotDispatched);
            }

            if (arguments.Count > 0 && arguments[0] == "wait-for")
            {
                return await endpoint.WaitForAsync(arguments, cancellationToken)
                    .ConfigureAwait(false);
            }

            return endpoint.Success(arguments);
        };
        await using JobStore jobs = new();

        LibTmuxException failure = await Assert.ThrowsAsync<LibTmuxException>(() =>
            jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                "echo maybe-started",
                suppressHistory: true,
                token));
        string retainedId = Assert.IsType<string>(
            failure.Data[JobStore.RecoveryJobIdDataKey]);

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        TmuxTransportException enterFailure = Assert.IsType<TmuxTransportException>(
            failure.InnerException);
        Assert.Equal(TmuxDispatchState.NotDispatched, enterFailure.Dispatch);
        Assert.Equal(2, Volatile.Read(ref sendStage));
        Assert.Equal(jobId, retainedId);
        JobInfo retained = Assert.Single(jobs.List().Jobs);
        Assert.Equal(retainedId, retained.JobId);
        Assert.Equal(JobState.Running, retained.State);
        Assert.NotNull(jobs.Resolve(retainedId, null).Watcher);
    }

    [Fact]
    public async Task Unknown_baseline_failure_does_not_publish_an_unstarted_job()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("baseline-failed", new ServerGeneration(555, 5505));
        endpoint.Handler = (arguments, _) =>
            arguments.Contains("capture-pane", StringComparer.Ordinal)
                ? Task.FromException<TmuxCommandResult>(new TmuxTransportException(
                    "The baseline capture pipe failed.",
                    arguments,
                    TmuxDispatchState.Unknown))
                : Task.FromResult(endpoint.Success(arguments));
        await using JobStore jobs = new();

        TmuxTransportException failure = await Assert.ThrowsAsync<TmuxTransportException>(
            () => jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                "echo never-dispatched",
                suppressHistory: true,
                token));

        Assert.Equal(TmuxDispatchState.Unknown, failure.Dispatch);
        Assert.False(failure.Data.Contains(JobStore.RecoveryJobIdDataKey));
        Assert.Equal(0, jobs.List().TotalJobs);
        Assert.DoesNotContain(
            endpoint.Commands,
            arguments => arguments.Contains("send-keys", StringComparer.Ordinal));
    }

    [Fact]
    public async Task Cancelled_start_removes_the_unissued_handle()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("cancelled", new ServerGeneration(606, 6006));
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        endpoint.Handler = async (arguments, cancellationToken) =>
        {
            if (arguments.Contains("send-keys", StringComparer.Ordinal))
            {
                sendStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return endpoint.Success(arguments);
        };
        await using JobStore jobs = new();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task<JobInfo> starting = jobs.StartAsync(
            endpoint.Server,
            endpoint.Pane,
            "echo cancelled",
            suppressHistory: true,
            cancellation.Token);
        await sendStarted.Task.WaitAsync(token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Equal(0, jobs.List().TotalJobs);
    }

    [Fact]
    public async Task Starting_job_is_not_visible_until_dispatch_commits()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("starting", new ServerGeneration(656, 6506));
        var sendStarted = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        endpoint.Handler = async (arguments, cancellationToken) =>
        {
            if (arguments.Contains("send-keys", StringComparer.Ordinal)
                && arguments.Any(argument => argument.Contains("lt_r_", StringComparison.Ordinal)))
            {
                sendStarted.TrySetResult(ExtractRunId(arguments));
                await releaseSend.Task.WaitAsync(cancellationToken);
            }

            return endpoint.Success(arguments);
        };
        await using JobStore jobs = new();

        Task<JobInfo> starting = jobs.StartAsync(
            endpoint.Server,
            endpoint.Pane,
            "echo withheld",
            suppressHistory: true,
            token);
        string jobId = await sendStarted.Task.WaitAsync(token);

        Assert.Equal(0, jobs.List().TotalJobs);
        _ = Assert.Throws<McpException>(() => jobs.Get(jobId));
        _ = await Assert.ThrowsAsync<McpException>(
            () => jobs.CancelAsync(jobId, cancellationToken: token));
        Assert.DoesNotContain(
            endpoint.Commands,
            arguments => arguments.Contains("C-c", StringComparer.Ordinal));

        releaseSend.TrySetResult();
        JobInfo issued = await starting.WaitAsync(token);

        Assert.Equal(jobId, issued.JobId);
        Assert.Equal(1, jobs.List().TotalJobs);
        Assert.Equal(jobId, jobs.Resolve(jobId, null).JobId);
    }

    [Fact]
    public async Task Ambiguous_dispatch_cancellation_retains_a_collectable_job()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("ambiguous", new ServerGeneration(676, 6706))
        {
            CaptureLines = ["possibly-started output"],
        };
        string? jobId = null;
        int ambiguousSend = 0;
        endpoint.Handler = async (arguments, cancellationToken) =>
        {
            if (arguments.Contains("send-keys", StringComparer.Ordinal)
                && arguments.Any(argument => argument.Contains("lt_r_", StringComparison.Ordinal))
                && Interlocked.CompareExchange(ref ambiguousSend, 1, 0) == 0)
            {
                jobId = ExtractRunId(arguments);
                endpoint.MarkJobDispatched();
                throw new TmuxOperationCanceledException(
                    "The tmux client was cancelled after launch.",
                    new CancellationToken(canceled: true),
                    commandMayHaveExecuted: true,
                    clientProcessId: 1234);
            }

            if (arguments.Count > 0 && arguments[0] == "wait-for")
            {
                return await endpoint.WaitForAsync(arguments, cancellationToken)
                    .ConfigureAwait(false);
            }

            return endpoint.Success(arguments);
        };
        await using JobStore jobs = new();
        await using PaneActivityHub activity = new();
        using TmuxConnectionAccessor connections = new(endpoint.Server);
        var write = new WriteTools(connections, new ServerPolicy(), activity, jobs);

        TmuxOperationCanceledException cancelled = await Assert.ThrowsAsync<
            TmuxOperationCanceledException>(() => jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                "echo maybe-started",
                suppressHistory: true,
                token));
        string retainedId = Assert.IsType<string>(
            cancelled.Data[JobStore.RecoveryJobIdDataKey]);

        Assert.Equal(jobId, retainedId);
        JobInfo retained = Assert.Single(jobs.List().Jobs);
        Assert.Equal(retainedId, retained.JobId);
        Assert.Equal(JobState.Running, retained.State);

        JobReport report = await write.JobAsync(
            retainedId,
            cancellationToken: token);
        Assert.Contains("possibly-started output", report.Output.Lines);

        JobInfo stopped = await jobs.CancelAsync(
            retainedId,
            cancellationToken: token);
        Assert.Equal(JobState.Cancelled, stopped.State);
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("unknown")]
    [InlineData("dispatched")]
    public async Task Non_definitive_dispatch_failures_retain_a_recovery_handle(
        string failureKind)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("dispatch-failed", new ServerGeneration(686, 6806));
        string? jobId = null;
        endpoint.Handler = async (arguments, cancellationToken) =>
        {
            if (arguments.Contains("send-keys", StringComparer.Ordinal)
                && arguments.Any(argument => argument.Contains("lt_r_", StringComparison.Ordinal)))
            {
                jobId = ExtractRunId(arguments);
                endpoint.MarkJobDispatched();
                if (failureKind == "cleanup")
                {
                    throw new TmuxCleanupException(
                        "The cancelled client could not be cleaned up.",
                        new OperationCanceledException(
                            "cancelled after launch",
                            new CancellationToken(canceled: true)),
                        clientProcessId: 2345,
                        new IOException("cleanup failed"));
                }

                if (failureKind == "unknown")
                {
                    throw new TmuxTransportException(
                        "The client pipe failed after launch.",
                        arguments,
                        TmuxDispatchState.Unknown,
                        new IOException("pipe failed"));
                }

                return FakeEndpoint.Result(
                    arguments,
                    exitCode: 1,
                    standardError: "tmux reported a dispatched failure\n");
            }

            if (arguments.Count > 0 && arguments[0] == "wait-for")
            {
                return await endpoint.WaitForAsync(arguments, cancellationToken)
                    .ConfigureAwait(false);
            }

            return endpoint.Success(arguments);
        };
        await using JobStore jobs = new();

        LibTmuxException failure = await Assert.ThrowsAnyAsync<LibTmuxException>(
            () => jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                $"echo {failureKind}",
                suppressHistory: true,
                token));
        string retainedId = Assert.IsType<string>(
            failure.Data[JobStore.RecoveryJobIdDataKey]);

        Assert.NotEqual(TmuxDispatchState.NotDispatched, failure.Dispatch);
        Assert.Equal(jobId, retainedId);
        JobInfo retained = Assert.Single(jobs.List().Jobs);
        Assert.Equal(retainedId, retained.JobId);
        Assert.Equal(JobState.Running, retained.State);
        Assert.NotNull(jobs.Resolve(retainedId, null).Watcher);
    }

    [Fact]
    public async Task Running_jobs_apply_backpressure_at_the_store_capacity()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("capacity", new ServerGeneration(707, 7007));
        await using JobStore jobs = new();

        for (int index = 0; index < JobStore.Capacity; index++)
        {
            _ = await jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                $"echo {index}",
                suppressHistory: true,
                token);
        }

        McpException full = await Assert.ThrowsAsync<McpException>(() => jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                "echo one-too-many",
                suppressHistory: true,
                token));

        Assert.Contains(
            JobStore.Capacity.ToString(CultureInfo.InvariantCulture),
            full.Message,
            StringComparison.Ordinal);
        Assert.Equal(JobStore.Capacity, jobs.List().TotalJobs);
    }

    [Fact]
    public async Task Cancelled_jobs_retain_capacity_until_their_watchers_end()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("cancel-capacity", new ServerGeneration(757, 7507));
        await using JobStore jobs = new();
        Task? firstWatcher = null;
        string? firstChannel = null;

        for (int index = 0; index < JobStore.Capacity; index++)
        {
            JobInfo started = await jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                $"ignore-sigint {index}",
                suppressHistory: true,
                token);
            JobInfo cancelled = await jobs.CancelAsync(
                started.JobId,
                cancellationToken: token);
            Assert.Equal(JobState.Cancelled, cancelled.State);
            Task watcher = Assert.IsAssignableFrom<Task>(
                jobs.Resolve(started.JobId, null).Watcher);
            firstWatcher ??= watcher;
            firstChannel ??= $"lt_r_{started.JobId}";
            Assert.False(watcher.IsCompleted);
        }

        McpException full = await Assert.ThrowsAsync<McpException>(() => jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                "ignore-sigint overflow",
                suppressHistory: true,
                token));

        Assert.Contains(
            JobStore.Capacity.ToString(CultureInfo.InvariantCulture),
            full.Message,
            StringComparison.Ordinal);
        Assert.Equal(JobStore.Capacity, jobs.List().TotalJobs);
        Assert.All(jobs.List().Jobs, job => Assert.Equal(JobState.Cancelled, job.State));

        await endpoint.Server.WaitForAsync(
            new WaitForRequest(firstChannel!, TmuxWaitMode.Signal),
            token);
        await Assert.IsAssignableFrom<Task>(firstWatcher).WaitAsync(token);
        _ = await jobs.StartAsync(
            endpoint.Server,
            endpoint.Pane,
            "replacement-after-watcher",
            suppressHistory: true,
            token);

        Assert.Equal(JobStore.Capacity, jobs.List().TotalJobs);
    }

    [Fact]
    public async Task Job_output_fits_the_complete_escaped_protocol_envelope()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("escaped-output", new ServerGeneration(767, 7607))
        {
            CaptureLines = [string.Concat(Enumerable.Repeat("\"\\", 4_000))],
        };
        await using JobStore jobs = new();
        await using PaneActivityHub activity = new();
        using TmuxConnectionAccessor connections = new(endpoint.Server);
        var write = new WriteTools(
            connections,
            new ServerPolicy { MaxBytes = 4_000 },
            activity,
            jobs);
        JobInfo started = await jobs.StartAsync(
            endpoint.Server,
            endpoint.Pane,
            "escape-heavy-output",
            suppressHistory: true,
            token);

        JobReport report = await write.JobAsync(
            started.JobId,
            cancellationToken: token);

        Assert.True(report.Output.Truncated);
        Assert.NotEmpty(report.Output.Lines);
        Assert.True(
            Utf8JsonBudget.GetStructuredToolResultByteCount(report, ToolJson.Options) <= 4_000);
    }

    [Fact]
    public async Task First_collection_starts_at_the_pre_dispatch_pane_baseline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("output-baseline", new ServerGeneration(772, 7702))
        {
            PreJobCaptureLines = ["old prompt"],
            CaptureLines = ["old prompt", "job line one", "job line two"],
        };
        await using JobStore jobs = new();
        await using PaneActivityHub activity = new();
        using TmuxConnectionAccessor connections = new(endpoint.Server);
        var write = new WriteTools(connections, new ServerPolicy(), activity, jobs);

        JobInfo started = await jobs.StartAsync(
            endpoint.Server,
            endpoint.Pane,
            "print-job-lines",
            suppressHistory: true,
            token);
        JobReport report = await write.JobAsync(
            started.JobId,
            cancellationToken: token);

        Assert.DoesNotContain("old prompt", report.Output.Lines);
        Assert.Equal(["job line one", "job line two"], report.Output.Lines);
        Assert.False(report.LinesMissed);
    }

    [Fact]
    public async Task Rejected_job_response_does_not_advance_output_cursor()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new(
            new string('s', 6_000),
            new ServerGeneration(777, 7707))
        {
            CaptureLines = ["retry-output"],
        };
        await using JobStore jobs = new();
        await using PaneActivityHub activity = new();
        using TmuxConnectionAccessor connections = new(endpoint.Server);
        var small = new WriteTools(
            connections,
            new ServerPolicy { MaxBytes = 4_000 },
            activity,
            jobs);
        var large = new WriteTools(
            connections,
            new ServerPolicy { MaxBytes = 128_000 },
            activity,
            jobs);
        JobInfo started = await jobs.StartAsync(
            endpoint.Server,
            endpoint.Pane,
            "print-once",
            suppressHistory: true,
            token);

        string baseline;
        using (JobStore.StoredJob.OutputLease output = await jobs
            .Resolve(started.JobId, null)
            .AcquireOutputAsync(token))
        {
            baseline = Assert.IsType<string>(output.Cursor);
        }

        McpException tooLarge = await Assert.ThrowsAsync<McpException>(
            () => small.JobAsync(started.JobId, cancellationToken: token));
        using (JobStore.StoredJob.OutputLease output = await jobs
            .Resolve(started.JobId, null)
            .AcquireOutputAsync(token))
        {
            Assert.Equal(baseline, output.Cursor);
        }

        JobReport retried = await large.JobAsync(
            started.JobId,
            cancellationToken: token);

        Assert.Contains("cannot fit", tooLarge.Message, StringComparison.Ordinal);
        Assert.Contains("retry-output", retried.Output.Lines);
    }

    [Fact]
    public async Task Terminal_signal_interrupts_a_silent_activity_wait()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("silent-finish", new ServerGeneration(787, 7807));
        JobStore.StoredJob job = endpoint.Job("silent-job");
        var activityStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activityCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> WaitForSilentActivity(CancellationToken cancellationToken)
        {
            activityStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                activityCancelled.TrySetResult();
                throw;
            }
        }

        Task<bool> waiting = WriteTools.WaitForTerminalOrActivityAsync(
            job,
            WaitForSilentActivity,
            token);
        await activityStarted.Task.WaitAsync(token);
        Assert.False(waiting.IsCompleted);

        Assert.True(job.TryFinish(JobState.Exited, 0));

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(1), token));
        await activityCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1), token);
    }

    [Fact]
    public async Task A_tmux_watcher_failure_says_why_the_job_was_lost()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("watch-tmux-fault", new ServerGeneration(809, 8009));
        endpoint.Handler = (arguments, _) =>
            arguments.Count > 0 && arguments[0] == "wait-for"
                ? Task.FromException<TmuxCommandResult>(
                    new TmuxTransportException("the client went away", arguments))
                : Task.FromResult(endpoint.Success(arguments));
        var logger = new RecordingLogger();
        await using JobStore jobs = new(logger);

        JobInfo started = await jobs.StartAsync(
            endpoint.Server,
            endpoint.Pane,
            "echo watched",
            suppressHistory: true,
            token);
        Task watcher = Assert.IsAssignableFrom<Task>(jobs.Resolve(started.JobId, null).Watcher);
        await watcher.WaitAsync(token);

        Assert.Equal(JobState.Lost, jobs.Get(started.JobId).State);
        Assert.Contains(
            logger.Entries,
            entry => entry.EventId.Id == 9 && entry.Error is TmuxTransportException);
    }

    [Fact]
    public async Task Unexpected_watcher_failure_is_observed_and_marks_the_job_lost()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("watch-fault", new ServerGeneration(808, 8008));
        endpoint.Handler = (arguments, _) =>
            arguments.Count > 0 && arguments[0] == "wait-for"
                ? Task.FromException<TmuxCommandResult>(new InvalidOperationException("watch exploded"))
                : Task.FromResult(endpoint.Success(arguments));
        var logger = new RecordingLogger();
        await using JobStore jobs = new(logger);

        JobInfo started = await jobs.StartAsync(
            endpoint.Server,
            endpoint.Pane,
            "echo watched",
            suppressHistory: true,
            token);
        Task watcher = Assert.IsAssignableFrom<Task>(jobs.Resolve(started.JobId, null).Watcher);
        await watcher.WaitAsync(token);

        Assert.Equal(JobState.Lost, jobs.Get(started.JobId).State);
        Assert.Contains(
            logger.Entries,
            entry => entry.EventId.Id == 9 && entry.Error is InvalidOperationException);
    }

    [Fact]
    public async Task Disposal_withdraws_the_tmux_waiter_before_it_finishes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("dispose-withdraw", new ServerGeneration(889, 8809));
        JobStore jobs = new();
        try
        {
            JobInfo started = await jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                "echo watched",
                suppressHistory: true,
                token);
            string channel = $"lt_r_{started.JobId}";
            await endpoint.WaitUntilRegisteredAsync(channel, token);

            await jobs.DisposeAsync().AsTask().WaitAsync(token);
            await endpoint.Server.WaitForAsync(
                new WaitForRequest(channel, TmuxWaitMode.Signal),
                token);

            await using TmuxWaitChannel next = endpoint.Server.OpenWaitChannel(channel);
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(1), token));
        }
        finally
        {
            await jobs.DisposeAsync();
        }
    }

    [Fact]
    public async Task Convenience_tools_have_async_shutdown_without_taking_a_supplied_store()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("tools-dispose", new ServerGeneration(899, 8909));
        endpoint.Handler = (arguments, _) => Task.FromResult(endpoint.Success(arguments));
        await using JobStore jobs = new();
        WriteTools tools = McpTools.Writing(endpoint.Server, jobs: jobs);

        IAsyncDisposable lifetime = Assert.IsAssignableFrom<IAsyncDisposable>(tools);
        await lifetime.DisposeAsync();
        await lifetime.DisposeAsync();

        JobInfo started = await jobs.StartAsync(
            endpoint.Server,
            endpoint.Pane,
            "echo still-open",
            suppressHistory: true,
            token);
        await Assert.IsAssignableFrom<Task>(jobs.Resolve(started.JobId, null).Watcher!)
            .WaitAsync(token);

        Assert.Equal(JobState.Exited, jobs.Get(started.JobId).State);
    }

    [Fact]
    public async Task Disposal_withdraws_then_waits_for_detached_watchers()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint endpoint = new("dispose", new ServerGeneration(909, 9009));
        var withdrawalSeen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        endpoint.Handler = async (arguments, cancellationToken) =>
        {
            if (arguments.Count > 0 && arguments[0] == "wait-for")
            {
                if (arguments.Contains("-S", StringComparer.Ordinal))
                {
                    withdrawalSeen.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return await endpoint.WaitForAsync(arguments, cancellationToken)
                    .ConfigureAwait(false);
            }

            return endpoint.Success(arguments);
        };
        JobStore jobs = new();
        try
        {
            _ = await jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                "echo watched",
                suppressHistory: true,
                token);

            Task disposing = jobs.DisposeAsync().AsTask();
            await withdrawalSeen.Task.WaitAsync(token);
            Assert.False(disposing.IsCompleted);

            release.TrySetResult();
            await disposing.WaitAsync(token);
        }
        finally
        {
            release.TrySetResult();
            await jobs.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disposal_preserves_a_retired_watcher_failure_and_drains_the_rest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeEndpoint faulting = new("dispose-fault", new ServerGeneration(959, 9509));
        faulting.Handler = (arguments, _) =>
            arguments.Count > 0 && arguments[0] == "wait-for"
                ? Task.FromException<TmuxCommandResult>(
                    new InvalidOperationException("watch failed before the next start"))
                : Task.FromResult(faulting.Success(arguments));
        FakeEndpoint held = new("dispose-held", new ServerGeneration(960, 9510));
        var withdrawalSeen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        held.Handler = async (arguments, cancellationToken) =>
        {
            if (arguments.Count > 0 && arguments[0] == "wait-for")
            {
                if (arguments.Contains("-S", StringComparer.Ordinal))
                {
                    withdrawalSeen.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return await held.WaitForAsync(arguments, cancellationToken)
                    .ConfigureAwait(false);
            }

            return held.Success(arguments);
        };
        JobStore jobs = new(new ThrowingLogger());
        try
        {
            JobInfo first = await jobs.StartAsync(
                faulting.Server,
                faulting.Pane,
                "echo faulting",
                suppressHistory: true,
                token);
            Task faultedWatcher = Assert.IsAssignableFrom<Task>(
                jobs.Resolve(first.JobId, null).Watcher);
            Exception watcherFailure = await Assert.ThrowsAnyAsync<Exception>(
                () => faultedWatcher.WaitAsync(token));
            Assert.Contains("logger failed", watcherFailure.ToString(), StringComparison.Ordinal);

            _ = await jobs.StartAsync(
                held.Server,
                held.Pane,
                "echo held",
                suppressHistory: true,
                token);

            Task disposing = jobs.DisposeAsync().AsTask();
            await withdrawalSeen.Task.WaitAsync(token);
            Assert.False(disposing.IsCompleted);

            release.TrySetResult();
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(
                () => disposing.WaitAsync(token));
            Assert.Contains("logger failed", failure.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await jobs.DisposeAsync();
            }
            catch (Exception error) when (error.ToString().Contains(
                "logger failed",
                StringComparison.Ordinal))
            {
            }
        }
    }

    [Fact]
    public async Task Command_and_list_budgets_bound_stored_responses()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string longSocket = new('s', 900);
        FakeEndpoint endpoint = new(longSocket, new ServerGeneration(1001, 10001));
        await using JobStore jobs = new();
        string secret = "PASSWORD=secret-value";

        JobInfo kept = await jobs.StartAsync(
            endpoint.Server,
            endpoint.Pane,
            secret,
            suppressHistory: true,
            token);
        for (int index = 0; index < 3; index++)
        {
            _ = await jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                $"echo {index}",
                suppressHistory: true,
                token);
        }

        JobList bounded = jobs.List(4_000);
        int responseBytes = Utf8JsonBudget.GetStructuredToolResultByteCount(
            bounded,
            ToolJson.Options);

        Assert.Equal(4, bounded.TotalJobs);
        Assert.True(bounded.Truncated);
        Assert.NotEmpty(bounded.Jobs);
        Assert.True(responseBytes <= 4_000, $"response used {responseBytes} bytes");
        Assert.DoesNotContain(
            secret,
            JsonSerializer.Serialize(kept, ToolJson.Options),
            StringComparison.Ordinal);

        string overBudget = new('x', 65);
        McpException tooLarge = await Assert.ThrowsAsync<McpException>(() => jobs.StartAsync(
                endpoint.Server,
                endpoint.Pane,
                overBudget,
                suppressHistory: true,
                maxCommandBytes: 64,
                cancellationToken: token));
        Assert.Contains("65", tooLarge.Message, StringComparison.Ordinal);
        Assert.Equal(4, jobs.List().TotalJobs);

        McpException tooSmall = Assert.Throws<McpException>(() => jobs.List(1));
        Assert.Contains("needs at least", tooSmall.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_job_records_the_socket_the_connection_resolved()
    {
        FakeEndpoint fromFactory = new(
            new ServerConnectionOptions(socketNameFactory: () => "jobs-factory"),
            new ServerGeneration(71, 701));
        FakeEndpoint fromDefault = new(
            new ServerConnectionOptions(),
            new ServerGeneration(72, 702));

        JobInfo factoryJob = fromFactory.Job("job-factory").Describe();
        JobInfo defaultJob = fromDefault.Job("job-default").Describe();

        Assert.Equal("jobs-factory", factoryJob.SocketName);
        Assert.Null(factoryJob.SocketPath);
        Assert.Equal("default", defaultJob.SocketName);

        fromFactory.Job("job-assert").RequireSocket("jobs-factory");
        McpException mismatch = Assert.Throws<McpException>(
            () => fromFactory.Job("job-assert").RequireSocket("jobs-elsewhere"));
        Assert.Contains("jobs-factory", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_job_records_the_socket_path_the_connection_resolved()
    {
        FakeEndpoint endpoint = new(
            new ServerConnectionOptions(socketPath: "relative-socket"),
            new ServerGeneration(73, 703));

        JobInfo described = endpoint.Job("job-path").Describe();

        Assert.Null(described.SocketName);
        Assert.Equal(Path.GetFullPath("relative-socket"), described.SocketPath);
    }

    private delegate Task<TmuxCommandResult> CommandHandler(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    private sealed class FakeEndpoint
    {
        private readonly TmuxConnection _connection;
        private readonly object _waitGate = new();
        private readonly Dictionary<string, TaskCompletionSource<TmuxCommandResult>> _waiters =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _waitRegistrations =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingSignals = new(StringComparer.Ordinal);
        private int _jobDispatched;

        internal FakeEndpoint(string socketName, ServerGeneration generation)
            : this(new ServerConnectionOptions(socketName: socketName), generation)
        {
        }

        internal FakeEndpoint(ServerConnectionOptions options, ServerGeneration generation)
        {
            Generation = generation;
            _connection = new TmuxConnection(
                options,
                FakeMultiplexer.AnsweringVersion(ExecuteAsync));
            Server = new Server(_connection, generation, "tmux 3.7");
            Pane = new Pane(
                Server,
                _connection,
                generation,
                new PaneId(1),
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["pane_id"] = "%1",
                    ["pane_width"] = "80",
                    ["pane_height"] = "24",
                });
        }

        internal ConcurrentQueue<string[]> Commands { get; } = new();

        internal ServerGeneration Generation { get; }

        internal Server Server { get; }

        internal Pane Pane { get; }

        internal IReadOnlyList<string> CaptureLines { get; init; } = [];

        internal IReadOnlyList<string> PreJobCaptureLines { get; init; } = [];

        internal CommandHandler? Handler { get; set; }

        internal JobStore.StoredJob Job(string jobId) => new(
            jobId,
            Server,
            Pane,
            commandBytes: 4,
            token: new WriteTools.RunToken("run-token"));

        internal TmuxCommandResult Success(IReadOnlyList<string> arguments) =>
            Result(
                arguments,
                exitCode: 0,
                standardOutput: Output(arguments));

        internal void MarkJobDispatched() => Volatile.Write(ref _jobDispatched, 1);

        internal static TmuxCommandResult Result(
            IReadOnlyList<string> arguments,
            int exitCode,
            string standardOutput = "",
            string standardError = "")
        {
            byte[] stdout = Encoding.UTF8.GetBytes(standardOutput);
            byte[] stderr = Encoding.UTF8.GetBytes(standardError);
            return new TmuxCommandResult(
                arguments,
                exitCode,
                stdout,
                stderr,
                Utf8BackslashDecoder.ProjectOutputLines(stdout),
                Utf8BackslashDecoder.ProjectErrorLines(stderr));
        }

        private async Task<TmuxCommandResult> ExecuteAsync(
            TmuxCommandRequest request,
            CancellationToken cancellationToken)
        {
            string[] arguments = [.. request.LogicalArguments];
            Commands.Enqueue(arguments);
            TmuxCommandResult result;
            if (Handler is not null)
            {
                result = await Handler(arguments, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (arguments.Length > 0 && arguments[0] == "wait-for")
                {
                    result = await WaitForAsync(arguments, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    result = Success(arguments);
                }
            }

            if (result.ExitCode == 0 && arguments.Contains("send-keys", StringComparer.Ordinal))
            {
                Volatile.Write(ref _jobDispatched, 1);
            }

            return result;
        }

        internal Task<TmuxCommandResult> WaitForAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            string channel = arguments[^1];
            lock (_waitGate)
            {
                if (arguments.Contains("-S", StringComparer.Ordinal))
                {
                    if (_waiters.Remove(
                            channel,
                            out TaskCompletionSource<TmuxCommandResult>? registeredWaiter))
                    {
                        registeredWaiter.TrySetResult(Success(arguments));
                    }
                    else
                    {
                        _pendingSignals.Add(channel);
                    }

                    return Task.FromResult(Success(arguments));
                }

                if (_pendingSignals.Remove(channel))
                {
                    return Task.FromResult(Success(arguments));
                }

                var newWaiter = new TaskCompletionSource<TmuxCommandResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(channel, newWaiter);
                if (_waitRegistrations.Remove(channel, out TaskCompletionSource? registered))
                {
                    registered.TrySetResult();
                }

                return newWaiter.Task.WaitAsync(cancellationToken);
            }
        }

        internal Task WaitUntilRegisteredAsync(string channel, CancellationToken cancellationToken)
        {
            lock (_waitGate)
            {
                if (_waiters.ContainsKey(channel))
                {
                    return Task.CompletedTask;
                }

                var registered = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _waitRegistrations.Add(channel, registered);
                return registered.Task.WaitAsync(cancellationToken);
            }
        }

        private static bool IsGuarded(IReadOnlyList<string> arguments) =>
            arguments.Count > 2
            && arguments[0] == "display-message"
            && arguments[2] == "#{pid}:#{start_time}";

        private string Output(IReadOnlyList<string> arguments)
        {
            string commandOutput;
            if (arguments.Count > 0
                && arguments.Any(argument => argument.Contains(
                    "#{history_size}",
                    StringComparison.Ordinal)))
            {
                commandOutput = "4242\t0\t50000\t24\t0\t0\t0\n";
            }
            else if (arguments.Contains("capture-pane", StringComparer.Ordinal))
            {
                IReadOnlyList<string> lines = Volatile.Read(ref _jobDispatched) == 0
                    ? PreJobCaptureLines
                    : CaptureLines;
                commandOutput = lines.Count == 0
                    ? string.Empty
                    : string.Join('\n', lines) + "\n";
            }
            else
            {
                commandOutput = string.Empty;
            }

            return IsGuarded(arguments)
                ? $"{Generation.ProcessId}:{Generation.StartTime}\n{commandOutput}"
                : commandOutput;
        }
    }

    private static string ExtractRunId(IReadOnlyList<string> arguments)
    {
        string payload = Assert.Single(
            arguments,
            argument => argument.Contains("lt_r_", StringComparison.Ordinal));
        int start = payload.IndexOf("lt_r_", StringComparison.Ordinal) + "lt_r_".Length;
        return payload.Substring(start, 10);
    }

    private sealed class RecordingLogger : ILogger
    {
        internal ConcurrentQueue<(EventId EventId, Exception? Error)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((eventId, exception));
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger failed");
    }
}
