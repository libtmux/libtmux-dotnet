using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LibTmux.Mcp;

/// <content>Commands that outlive the call that started them.</content>
[UnsupportedOSPlatform("windows")]
public sealed partial class WriteTools
{
    /// <summary>Starts a command without waiting for it.</summary>
    /// <param name="command">The shell command.</param>
    /// <param name="paneId">The pane, or null for the active one.</param>
    /// <param name="suppressHistory">Whether to keep the command out of shell history.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels sending the command.</param>
    /// <returns>The handle to collect it with.</returns>
    [McpServerTool(Name = "tmux_start_job", Destructive = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Start a shell command in a pane and return a job handle IMMEDIATELY, without "
        + "waiting. Use for anything that may run longer than a few seconds — a build, "
        + "a test suite, a deploy — so you can do other work and collect the result "
        + "later with tmux_job. The command keeps running in the pane regardless of "
        + "what you do next. If cancellation races dispatch, call tmux_list_jobs: "
        + "a possibly started command keeps a recoverable handle.")]
    public async Task<JobInfo> StartJobAsync(
        [Description("The shell command to run.")] string command,
        [Description("The pane id, such as %1. Omit for the active pane.")]
        string? paneId = null,
        [Description("Keep the command out of the shell's history. Best-effort.")]
        bool suppressHistory = true,
        [Description("The tmux socket to use. Omit for the default server.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
    {
        Server server = await ServerAsync(socketName, cancellationToken).ConfigureAwait(false);
        Pane pane = await TmuxTargets.PaneAsync(server, paneId, cancellationToken)
            .ConfigureAwait(false);
        return await _jobs.StartAsync(
                server,
                pane,
                command,
                suppressHistory,
                _policy.MaxBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads how a job is doing and what it has printed.</summary>
    /// <param name="jobId">The handle.</param>
    /// <param name="waitSeconds">How long to wait for it to finish, if it has not.</param>
    /// <param name="maxLines">The most output lines to answer.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="progress">Reports that the job is still running.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The job and whatever is new in its pane.</returns>
    /// <remarks>
    /// Waiting here is event-driven rather than a sleep loop, so asking with a
    /// <paramref name="waitSeconds" /> costs no more than asking without one
    /// and returns the instant the command finishes.
    /// </remarks>
    [McpServerTool(Name = "tmux_job", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Read a background job's state, exit status, and whatever its pane has printed "
        + "SINCE THE LAST TIME you asked. Optionally wait a few seconds for it to "
        + "finish first. Call this instead of capturing the pane: it returns only new "
        + "output, so watching a long job stays cheap.")]
    public async Task<JobReport> JobAsync(
        [Description("The job handle from tmux_start_job.")] string jobId,
        [Description(
            "Seconds to wait for the job to finish before answering. Omit to answer "
            + "at once with whatever it is doing now.")]
        double? waitSeconds = null,
        [Description("The most output lines to return, newest kept.")]
        int? maxLines = null,
        [Description(
            "The originating tmux socket. Omit to use the endpoint recorded by "
            + "tmux_start_job; a supplied socket must match it.")]
        string? socketName = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        JobStore.StoredJob stored = _jobs.Resolve(jobId, socketName);
        JobInfo job = stored.Describe();
        Pane pane = stored.Pane;

        if (waitSeconds is double seconds && job.State == JobState.Running)
        {
            TimeSpan budget = _policy.EffectiveTimeout(TimeSpan.FromSeconds(seconds));
            IAsyncDisposable lease = await _activity
                .WatchAsync(pane, cancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable _ = lease.ConfigureAwait(false);
            await WaitForFinishAsync(
                    stored,
                    pane,
                    budget,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            job = stored.Describe();
        }

        using JobStore.StoredJob.OutputLease output = await stored
            .AcquireOutputAsync(cancellationToken)
            .ConfigureAwait(false);
        TailCursor? cursor = TailCursor.Decode(output.Cursor, pane);
        PaneRead read = cursor is null
            ? await PaneReader.ReadVisibleAsync(pane, null, cancellationToken).ConfigureAwait(false)
            : await PaneReader.ReadSinceAsync(pane, cursor, cancellationToken).ConfigureAwait(false);

        JobReport report = FitJobReport(
            stored.Describe(),
            PaneText.Scrub(read.Lines, pane.Width),
            maxLines ?? _policy.MaxLines,
            read.LinesMissed);
        string nextCursor = TailCursor.Build(pane, read.State, read.CursorRows).Encode();
        output.Advance(nextCursor);
        return report;
    }

    /// <summary>Lists the jobs this server still remembers.</summary>
    /// <returns>The jobs, most recently started first.</returns>
    [McpServerTool(Name = "tmux_list_jobs", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "List the background jobs this server started and still remembers, newest "
        + "first. A job is forgotten when the server restarts, but its command keeps "
        + "running in its pane.")]
    public JobList ListJobs() => _jobs.List(_policy.MaxBytes);

    /// <summary>Interrupts a job.</summary>
    /// <param name="jobId">The handle.</param>
    /// <param name="socketName">The tmux socket, or null for the default.</param>
    /// <param name="cancellationToken">Cancels sending the interrupt.</param>
    /// <returns>The job.</returns>
    [McpServerTool(Name = "tmux_cancel_job", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Interrupt a background job by sending its pane Ctrl-C. This is a request, not "
        + "a guarantee: a program that ignores SIGINT keeps running. Check the pane's "
        + "currentCommand afterwards to see whether it actually stopped.")]
    public async Task<JobInfo> CancelJobAsync(
        [Description("The job handle from tmux_start_job.")] string jobId,
        [Description(
            "The originating tmux socket. Omit to use the endpoint recorded by "
            + "tmux_start_job; a supplied socket must match it.")]
        string? socketName = null,
        CancellationToken cancellationToken = default)
        => await _jobs.CancelAsync(jobId, socketName, cancellationToken).ConfigureAwait(false);

    private async Task WaitForFinishAsync(
        JobStore.StoredJob job,
        Pane pane,
        TimeSpan budget,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        string paneId = pane.Id.ToString();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        DateTimeOffset deadline = started + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (job.State != JobState.Running)
            {
                return;
            }

            ReadTools.Report(
                progress,
                DateTimeOffset.UtcNow - started,
                budget,
                $"job {job.JobId} still running in {paneId}");
            object? signal = _activity.CaptureSignal(pane);
            if (job.State != JobState.Running)
            {
                return;
            }

            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (await WaitForTerminalOrActivityAsync(
                    job,
                    token => _activity.WaitForActivityAsync(
                        paneId,
                        signal,
                        remaining,
                        token),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }
        }
    }

    internal static async Task<bool> WaitForTerminalOrActivityAsync(
        JobStore.StoredJob job,
        Func<CancellationToken, Task<bool>> waitForActivity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(waitForActivity);
        cancellationToken.ThrowIfCancellationRequested();
        if (job.State != JobState.Running)
        {
            return true;
        }

        using CancellationTokenSource activityCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<bool> activity = waitForActivity(activityCancellation.Token);
        Task completed = await Task.WhenAny(job.Terminal, activity).ConfigureAwait(false);
        if (completed == job.Terminal)
        {
            await activityCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                _ = await activity.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (activityCancellation.IsCancellationRequested)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        _ = await activity.ConfigureAwait(false);
        return job.State != JobState.Running;
    }

    private JobReport FitJobReport(
        JobInfo job,
        IReadOnlyList<string> lines,
        int maxLines,
        bool linesMissed) =>
        StructuredTextResultBudget.Fit(
            lines,
            maxLines,
            _policy.MaxBytes,
            output => new JobReport(job, output, linesMissed),
            "job result");
}
