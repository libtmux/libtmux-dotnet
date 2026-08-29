using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <summary>Commands that keep running after the call that started them returned.</summary>
/// <remarks>
/// <para>
/// A wait bounded by the server's ceiling is the right shape for a command
/// that takes seconds. It is the wrong shape for a build: the model spends its
/// whole turn asleep, and the protocol gives it no way to change its mind
/// halfway. A job inverts that — starting one returns immediately with a
/// handle, the command runs on in the pane, and the model does something else
/// and collects the result when it is ready.
/// </para>
/// <para>
/// tmux is what keeps the command alive, not this process. What is held here
/// is only the bookkeeping needed to recognise the finish and read the output:
/// if this server is restarted the command carries on, and only the handle is
/// lost.
/// </para>
/// <para>
/// The store is asynchronous all the way down, disposal included. Whatever
/// owns one disposes it with <c>await using</c>.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class JobStore : IAsyncDisposable
{
    internal const int Capacity = 100;
    internal const string RecoveryJobIdDataKey = "LibTmux.Mcp.JobId";

    private readonly object _gate = new();
    private readonly Dictionary<string, StoredJob> _jobs = new(StringComparer.Ordinal);
    private readonly HashSet<StoredJob> _starting = [];
    private readonly HashSet<Task> _operations = [];
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Exception? _operationFailure;
    private Task? _disposeTask;
    private bool _stopping;

    /// <summary>Initializes the store.</summary>
    /// <param name="logger">Records how a job ended.</param>
    public JobStore(ILogger? logger = null) => _logger = logger;

    /// <inheritdoc />
    /// <remarks>
    /// Shutting down waits for tmux watchers to finish, so there is no
    /// synchronous disposal to offer: blocking on that wait is what deadlocks
    /// a caller holding a single-threaded context. A container holding this
    /// has to be disposed with <c>DisposeAsync</c>.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>Starts a command and answers a handle for it immediately.</summary>
    /// <param name="server">The server the pane belongs to.</param>
    /// <param name="pane">The pane to run in.</param>
    /// <param name="command">The shell command.</param>
    /// <param name="suppressHistory">Whether to keep the command out of shell history.</param>
    /// <param name="cancellationToken">Cancels sending the command.</param>
    /// <returns>The job.</returns>
    public Task<JobInfo> StartAsync(
        Server server,
        Pane pane,
        string command,
        bool suppressHistory,
        CancellationToken cancellationToken) =>
        StartAsync(
            server,
            pane,
            command,
            suppressHistory,
            ServerPolicy.DefaultMaxBytes,
            cancellationToken);

    internal Task<JobInfo> StartAsync(
        Server server,
        Pane pane,
        string command,
        bool suppressHistory,
        int maxCommandBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCommandBytes);
        RequireOwnership(server, pane);

        int commandBytes = Encoding.UTF8.GetByteCount(command);
        if (commandBytes > maxCommandBytes)
        {
            throw new McpException(
                $"The command is {commandBytes} UTF-8 bytes; the job input ceiling is "
                + $"{maxCommandBytes}. Put a longer script in a file and start that file instead.");
        }

        WriteTools.RunToken token = WriteTools.RunToken.Create();
        var job = new StoredJob(token.Id, server, pane, commandBytes, token);
        job.RequireToolResultsFit(maxCommandBytes);
        var reservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            ForgetLocked();
            if (_jobs.Count + _starting.Count >= Capacity)
            {
                throw new McpException(
                    $"This MCP server is already tracking {Capacity} live job operations. "
                    + "Wait for one of their tmux watchers to finish before starting another; "
                    + "cancellation retains its slot until then.");
            }

            _starting.Add(job);
            TrackLocked(reservation.Task);
        }

        return StartCoreAsync(
            server,
            pane,
            command,
            suppressHistory,
            job,
            reservation,
            cancellationToken);
    }

    /// <summary>Answers what a job is doing.</summary>
    /// <param name="jobId">The handle.</param>
    /// <returns>The job.</returns>
    /// <exception cref="McpException">No job has that handle.</exception>
    public JobInfo Get(string jobId) => Require(jobId).Describe();

    /// <summary>Answers the jobs that fit within one response.</summary>
    /// <param name="maxBytes">The complete UTF-8 response ceiling, including protocol reserve.</param>
    /// <returns>The jobs, most recently started first.</returns>
    /// <exception cref="McpException">The envelope cannot fit within the byte ceiling.</exception>
    public JobList List(int maxBytes = ServerPolicy.DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        StoredJob[] jobs;
        lock (_gate)
        {
            jobs = [.. _jobs.Values.OrderByDescending(job => job.StartedAt)];
        }

        JobInfo[] available = [.. jobs.Select(job => job.Describe())];
        JobList empty = new([], available.Length, available.Length > 0);
        int envelopeBytes = Utf8JsonBudget.GetStructuredToolResultByteCount(
            empty,
            ToolJson.Options);
        if (envelopeBytes > maxBytes)
        {
            throw new McpException(
                $"The job-list response needs at least {envelopeBytes} UTF-8 bytes; "
                + $"the configured ceiling is {maxBytes}.");
        }

        var kept = new List<JobInfo>(available.Length);
        foreach (JobInfo candidate in available)
        {
            JobList proposed = new(
                [.. kept, candidate],
                available.Length,
                kept.Count + 1 < available.Length);
            if (Utf8JsonBudget.GetStructuredToolResultByteCount(
                    proposed,
                    ToolJson.Options) > maxBytes)
            {
                break;
            }

            kept.Add(candidate);
        }

        return new JobList([.. kept], available.Length, kept.Count < available.Length);
    }

    /// <summary>Interrupts a job on the endpoint that started it.</summary>
    /// <param name="jobId">The handle.</param>
    /// <param name="socketName">An optional assertion about the originating socket.</param>
    /// <param name="cancellationToken">Cancels sending the interrupt.</param>
    /// <returns>The job.</returns>
    public Task<JobInfo> CancelAsync(
        string jobId,
        string? socketName = null,
        CancellationToken cancellationToken = default) =>
        Resolve(jobId, socketName).CancelAsync(cancellationToken);

    internal StoredJob Resolve(string jobId, string? socketName)
    {
        StoredJob job = Require(jobId);
        job.RequireSocket(socketName);
        return job;
    }

    private async Task<JobInfo> StartCoreAsync(
        Server server,
        Pane pane,
        string command,
        bool suppressHistory,
        StoredJob job,
        TaskCompletionSource reservation,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        bool dispatchAttempted = false;
        bool published = false;
        try
        {
            PaneRead baseline = await PaneReader
                .ReadVisibleAsync(pane, null, linked.Token)
                .ConfigureAwait(false);
            job.SetInitialCursor(
                TailCursor.Build(pane, baseline.State, baseline.CursorRows).Encode());

            dispatchAttempted = true;
            await WriteTools
                .SendRunPayloadAsync(
                    server,
                    pane,
                    command,
                    job.Token,
                    suppressHistory,
                    WriteTools.JobStatusMarkerLifetime,
                    linked.Token)
                .ConfigureAwait(false);

            Publish(job, server, pane);
            published = true;
            return job.Describe();
        }
        catch (TmuxOperationCanceledException error) when (
            dispatchAttempted && error.CommandMayHaveExecuted)
        {
            Publish(job, server, pane);
            published = true;
            error.Data[RecoveryJobIdDataKey] = job.JobId;
            throw;
        }
        catch (LibTmuxException error) when (
            dispatchAttempted && error.Dispatch != TmuxDispatchState.NotDispatched)
        {
            Publish(job, server, pane);
            published = true;
            error.Data[RecoveryJobIdDataKey] = job.JobId;
            throw;
        }
        catch
        {
            if (!published)
            {
                RejectStart(job);
            }

            throw;
        }
        finally
        {
            reservation.TrySetResult();
        }
    }

    private void Publish(StoredJob job, Server server, Pane pane)
    {
        Task watcher = WatchAsync(server, pane, job);
        job.Watcher = watcher;
        lock (_gate)
        {
            if (!_starting.Remove(job))
            {
                throw new InvalidOperationException("The job start reservation no longer exists.");
            }

            _jobs.Add(job.JobId, job);
            TrackLocked(watcher);
        }
    }

    private void RejectStart(StoredJob job)
    {
        job.TryFinish(JobState.Lost, null);
        lock (_gate)
        {
            _starting.Remove(job);
        }
    }

    private StoredJob Require(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        lock (_gate)
        {
            if (_jobs.TryGetValue(jobId.Trim(), out StoredJob? job))
            {
                return job;
            }
        }

        throw new McpException(
            $"No job '{jobId}' exists. Call tmux_list_jobs to see which do. A job is "
            + "forgotten when this server restarts, though the command itself keeps "
            + "running in its pane.");
    }

    private static void RequireOwnership(Server server, Pane pane)
    {
        Server owner;
        try
        {
            owner = pane.Server;
        }
        catch (IncompleteSnapshotException error)
        {
            throw new McpException(
                "The job pane has no exact server ownership. Resolve it from the server "
                + "that will start the command.",
                error);
        }

        string? serverEndpoint = server.Connection?.GetEndpointFingerprint();
        string? paneEndpoint = owner.Connection?.GetEndpointFingerprint();
        if (serverEndpoint is null
            || paneEndpoint is null
            || !string.Equals(serverEndpoint, paneEndpoint, StringComparison.Ordinal)
            || server.Generation != pane.Generation
            || owner.Generation != pane.Generation)
        {
            throw new McpException(
                $"Pane {pane.Id} belongs to a different tmux endpoint or server generation. "
                + "Resolve the pane from the same server passed to StartAsync.");
        }
    }

    private void ForgetLocked()
    {
        foreach (StoredJob stale in _jobs.Values
            .Where(job => job.CanReleaseSlot)
            .OrderBy(job => job.EndedAt ?? job.StartedAt))
        {
            if (_jobs.Count + _starting.Count < Capacity)
            {
                break;
            }

            _jobs.Remove(stale.JobId);
        }
    }

    private async Task WatchAsync(Server server, Pane pane, StoredJob job)
    {
        Exception? unexpected = null;
        try
        {
            await server.WaitForAsync(
                    new WaitForRequest(job.Token.Channel, TmuxWaitMode.Wait),
                    _shutdown.Token)
                .ConfigureAwait(false);

            int? status = await WriteTools
                .ReadStatusAsync(pane, job.Token, _shutdown.Token)
                .ConfigureAwait(false);
            job.TryFinish(JobState.Exited, status);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // The command belongs to tmux and survives this bookkeeping store.
        }
        catch (LibTmuxException)
        {
            job.TryFinish(JobState.Lost, null);
        }
        catch (Exception error)
        {
            unexpected = error;
            job.TryFinish(JobState.Lost, null);
        }

        if (_logger is not null && unexpected is not null)
        {
            Log.JobWatcherFailed(_logger, unexpected, job.JobId, job.PaneId);
        }

        if (_logger is not null && job.State != JobState.Running)
        {
            Log.JobEnded(_logger, job.JobId, job.PaneId, job.State);
        }
    }

    private void TrackLocked(Task operation)
    {
        foreach (Task completed in _operations
            .Where(static tracked => tracked.IsCompleted)
            .ToArray())
        {
            RecordFailureLocked(completed);
            _operations.Remove(completed);
        }

        _operations.Add(operation);
    }

    private void RecordFailureLocked(Task operation)
    {
        if (_operationFailure is null && operation.IsFaulted)
        {
            _operationFailure = operation.Exception;
        }
    }

    private async Task DisposeCoreAsync()
    {
        Exception? failure;
        lock (_gate)
        {
            failure = _operationFailure;
        }

        try
        {
            try
            {
                _shutdown.Cancel();
            }
            catch (Exception error)
            {
                failure ??= error;
            }

            while (true)
            {
                Task[] pending;
                lock (_gate)
                {
                    pending = [.. _operations];
                }

                if (pending.Length == 0)
                {
                    break;
                }

                Task all = Task.WhenAll(pending);
                try
                {
                    await all.ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failure ??= all.Exception ?? error;
                }

                lock (_gate)
                {
                    foreach (Task completed in pending)
                    {
                        RecordFailureLocked(completed);
                        _operations.Remove(completed);
                    }

                    failure ??= _operationFailure;
                }
            }
        }
        finally
        {
            _shutdown.Dispose();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    internal sealed class StoredJob
    {
        private static readonly Progress Running = new(JobState.Running, null, null);

        private readonly Channel<byte> _cancelGate = Gate();
        private readonly Channel<byte> _outputGate = Gate();
        private readonly TaskCompletionSource _terminal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _commandBytes;
        private readonly string _endpointFingerprint;
        private readonly string? _socketName;
        private readonly string? _socketPath;
        private Progress _progress = Running;
        private string? _cursor;

        internal StoredJob(
            string jobId,
            Server server,
            Pane pane,
            int commandBytes,
            WriteTools.RunToken token)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
            ArgumentNullException.ThrowIfNull(server);
            ArgumentNullException.ThrowIfNull(pane);
            ArgumentOutOfRangeException.ThrowIfNegative(commandBytes);

            JobId = jobId;
            Server = server;
            Pane = pane;
            PaneId = pane.Id.ToString();
            ServerGeneration = pane.Generation;
            _commandBytes = commandBytes;
            TmuxConnection connection = pane.Server.Connection
                ?? throw new IncompleteSnapshotException("connection", SnapshotDepth.Server);
            _endpointFingerprint = connection.GetEndpointFingerprint();
            Token = token;
            StartedAt = DateTimeOffset.UtcNow;

            (_socketName, _socketPath) = connection.ResolvedSocket;
        }

        internal string JobId { get; }

        internal string PaneId { get; }

        internal Server Server { get; }

        internal Pane Pane { get; }

        internal ServerGeneration ServerGeneration { get; }

        internal WriteTools.RunToken Token { get; }

        internal DateTimeOffset StartedAt { get; }

        internal DateTimeOffset? EndedAt => Volatile.Read(ref _progress).EndedAt;

        internal JobState State => Volatile.Read(ref _progress).State;

        internal Task? Watcher { get; set; }

        internal Task Terminal => _terminal.Task;

        internal bool CanReleaseSlot =>
            State != JobState.Running && Watcher is { IsCompleted: true };

        internal bool TryFinish(JobState state, int? exitStatus)
        {
            if (state == JobState.Running)
            {
                throw new ArgumentOutOfRangeException(nameof(state));
            }

            var terminal = new Progress(state, exitStatus, DateTimeOffset.UtcNow);
            bool changed = ReferenceEquals(
                Interlocked.CompareExchange(ref _progress, terminal, Running),
                Running);
            if (changed)
            {
                _terminal.TrySetResult();
            }

            return changed;
        }

        internal async Task<JobInfo> CancelAsync(CancellationToken cancellationToken)
        {
            _ = await _cancelGate.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State == JobState.Running)
                {
                    await Pane.SendKeysAsync(
                            new SendKeysRequest(text: "C-c", enter: false, literal: false),
                            cancellationToken)
                        .ConfigureAwait(false);
                    TryFinish(JobState.Cancelled, null);
                }

                return Describe();
            }
            finally
            {
                _cancelGate.Writer.TryWrite(0);
            }
        }

        internal async ValueTask<OutputLease> AcquireOutputAsync(
            CancellationToken cancellationToken)
        {
            _ = await _outputGate.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new OutputLease(this);
        }

        internal void SetInitialCursor(string cursor)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
            if (Interlocked.CompareExchange(ref _cursor, cursor, null) is not null)
            {
                throw new InvalidOperationException("The job output baseline was already set.");
            }
        }

        internal void RequireSocket(string? suppliedSocketName)
        {
            if (string.IsNullOrWhiteSpace(suppliedSocketName))
            {
                return;
            }

            string supplied = suppliedSocketName.Trim();
            if (_socketName is not null
                && string.Equals(supplied, _socketName, StringComparison.Ordinal))
            {
                return;
            }

            string endpoint = _socketPath is not null
                ? $"socket path '{_socketPath}'"
                : _socketName is not null
                    ? $"socket '{_socketName}'"
                    : "its recorded endpoint";
            throw new McpException(
                $"Job '{JobId}' belongs to {endpoint}, not supplied socket '{supplied}'. "
                + "Omit socketName to use the job's recorded endpoint.");
        }

        internal JobInfo Describe()
        {
            Progress progress = Volatile.Read(ref _progress);
            double elapsedSeconds = Math.Round(
                ((progress.EndedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalSeconds,
                3);
            return CreateDescription(progress, elapsedSeconds);
        }

        internal void RequireToolResultsFit(int maxBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
            string endpoint = _socketPath ?? _socketName ?? string.Empty;
            int endpointBytes = Encoding.UTF8.GetByteCount(endpoint);
            if (endpointBytes > maxBytes)
            {
                throw new McpException(
                    $"The job handle response cannot fit because its endpoint identity is "
                    + $"{endpointBytes} UTF-8 bytes; the configured ceiling is {maxBytes}. "
                    + $"Use a shorter socket name or raise {ServerPolicy.MaxBytesVariable} "
                    + "and restart the MCP server.");
            }

            JobInfo worstCase = CreateDescription(
                new Progress(JobState.Cancelled, int.MinValue, DateTimeOffset.MaxValue),
                double.MaxValue);
            int requiredBytes = Utf8JsonBudget.GetStructuredToolResultByteCount(
                worstCase,
                ToolJson.Options);
            if (requiredBytes > maxBytes)
            {
                throw new McpException(
                    $"The job handle response needs at least {requiredBytes} UTF-8 bytes; "
                    + $"the configured ceiling is {maxBytes}. Use a shorter socket name or "
                    + $"raise {ServerPolicy.MaxBytesVariable} and restart the MCP server.");
            }
        }

        private JobInfo CreateDescription(Progress progress, double elapsedSeconds) =>
            new(
                JobId: JobId,
                PaneId: PaneId,
                SocketName: _socketName,
                SocketPath: _socketPath,
                EndpointFingerprint: _endpointFingerprint,
                ServerGeneration: ServerGeneration,
                CommandBytes: _commandBytes,
                State: progress.State,
                ExitStatus: progress.ExitStatus,
                StartedAt: StartedAt,
                EndedAt: progress.EndedAt,
                ElapsedSeconds: elapsedSeconds);

        private sealed record Progress(
            JobState State,
            int? ExitStatus,
            DateTimeOffset? EndedAt);

        private static Channel<byte> Gate()
        {
            Channel<byte> gate = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });
            gate.Writer.TryWrite(0);
            return gate;
        }

        internal sealed class OutputLease : IDisposable
        {
            private StoredJob? _job;

            internal OutputLease(StoredJob job) => _job = job;

            internal string? Cursor => RequireJob()._cursor;

            internal void Advance(string cursor)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
                RequireJob()._cursor = cursor;
            }

            public void Dispose()
            {
                StoredJob? job = Interlocked.Exchange(ref _job, null);
                job?._outputGate.Writer.TryWrite(0);
            }

            private StoredJob RequireJob() =>
                _job ?? throw new ObjectDisposedException(nameof(OutputLease));
        }
    }
}
