using System.ComponentModel;
using System.Diagnostics;

namespace LibTmux.Internal;

internal interface ITmuxProcessLauncher
{
    public ITmuxProcessHandle Start(ProcessStartInfo startInfo);
}

internal interface ITmuxProcessHandle : IAsyncDisposable
{
    public int Id { get; }

    public Stream StandardOutput { get; }

    public Stream StandardError { get; }

    public int ExitCode { get; }

    public bool HasExited { get; }

    public void Kill();

    public Task WaitForExitAsync(CancellationToken cancellationToken = default);
}

internal sealed class TmuxProcessTransport
{
    private readonly string _executablePath;
    private readonly string[] _prefixArguments;
    private readonly TmuxTransportLimits _limits;
    private readonly ITmuxProcessLauncher _launcher;
    private readonly TimeProvider _timeProvider;
    private readonly Func<ProcessStartInfo, CancellationToken, ValueTask>? _beforeStart;
    internal TmuxProcessTransport(
        string executablePath,
        IReadOnlyList<string>? prefixArguments = null,
        TmuxTransportLimits? limits = null,
        Func<ProcessStartInfo, Process>? launcher = null,
        TimeProvider? timeProvider = null,
        Func<ProcessStartInfo, CancellationToken, ValueTask>? beforeStart = null)
        : this(
            executablePath,
            launcher is null ? new SystemProcessLauncher() : new DelegateProcessLauncher(launcher),
            prefixArguments,
            limits,
            timeProvider,
            beforeStart)
    {
    }

    internal TmuxProcessTransport(
        string executablePath,
        ITmuxProcessLauncher launcher,
        IReadOnlyList<string>? prefixArguments = null,
        TmuxTransportLimits? limits = null,
        TimeProvider? timeProvider = null,
        Func<ProcessStartInfo, CancellationToken, ValueTask>? beforeStart = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
        _prefixArguments = prefixArguments is null ? [] : [.. prefixArguments];
        _limits = limits ?? new TmuxTransportLimits();
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _beforeStart = beforeStart;
    }

    internal Task<TmuxCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(TmuxCommandRequest.Single(arguments), cancellationToken);

    internal async Task<TmuxCommandResult> ExecuteAsync(
        TmuxCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> encodedArguments = request.EncodeArguments();
        if (encodedArguments.Count > _limits.MaxArguments)
        {
            throw new TmuxTransportException(
                $"The command exceeds the {_limits.MaxArguments} argument limit.",
                request.LogicalArguments,
                TmuxDispatchState.NotDispatched);
        }

        ProcessStartInfo startInfo = CreateStartInfo(encodedArguments, request.PreventServerStart);
        cancellationToken.ThrowIfCancellationRequested();
        ITmuxProcessHandle process;
        try
        {
            if (_beforeStart is not null)
            {
                await _beforeStart(startInfo, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            process = _launcher.Start(startInfo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        {
            throw new TmuxCommandNotFoundException(
                $"The configured tmux executable '{_executablePath}' was not found.",
                _executablePath,
                error);
        }
        catch (FileNotFoundException error)
        {
            throw new TmuxCommandNotFoundException(
                $"The configured tmux executable '{_executablePath}' was not found.",
                _executablePath,
                error);
        }
        catch (Exception error)
        {
            throw new TmuxTransportException(
                "The tmux client process could not be started.",
                request.LogicalArguments,
                TmuxDispatchState.NotDispatched,
                error);
        }

        await using (process.ConfigureAwait(false))
        {
            Task? wait = null;
            Task<byte[]>? stdout = null;
            Task<byte[]>? stderr = null;
            try
            {
                wait = process.WaitForExitAsync(cancellationToken);
                stdout = ReadBoundedAsync(process.StandardOutput);
                stderr = ReadBoundedAsync(process.StandardError);
                await AwaitProcessAndPumpsAsync(
                    cancellationToken,
                    wait,
                    stdout,
                    stderr).ConfigureAwait(false);

                byte[] standardOutput = await stdout.ConfigureAwait(false);
                byte[] standardError = await stderr.ConfigureAwait(false);
                var result = new TmuxCommandResult(
                    request.LogicalArguments,
                    process.ExitCode,
                    standardOutput,
                    standardError,
                    Utf8BackslashDecoder.ProjectOutputLines(standardOutput),
                    Utf8BackslashDecoder.ProjectErrorLines(standardError));
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
            {
                var originalCancellation = new OperationCanceledException(
                    "The tmux client operation was canceled after process start.",
                    error,
                    cancellationToken);
                try
                {
                    await CleanupAsync(
                            process,
                            primaryFailure: error,
                            primaryOperation: null,
                            stdout,
                            stderr)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    throw new TmuxCleanupException(
                        "The canceled tmux client could not be cleaned up.",
                        originalCancellation,
                        process.Id,
                        cleanupFailure);
                }

                throw new TmuxOperationCanceledException(
                    "The tmux client operation was canceled after process start.",
                    cancellationToken,
                    commandMayHaveExecuted: true,
                    process.Id,
                    error);
            }
            catch (Exception error)
            {
                Exception primaryFailure = error is OperationTaskException operationFailure
                    ? operationFailure.Failure
                    : error;
                Task? primaryOperation = error is OperationTaskException failedOperation
                    ? failedOperation.Operation
                    : null;
                try
                {
                    await CleanupAsync(
                            process,
                            primaryFailure,
                            primaryOperation,
                            stdout,
                            stderr)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    primaryFailure = new AggregateException(primaryFailure, cleanupFailure);
                }

                throw new TmuxTransportException(
                    "The tmux client process or one of its output streams failed.",
                    request.LogicalArguments,
                    primaryFailure);
            }

        }
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> encodedArguments, bool preventServerStart)
    {
        var startInfo = new ProcessStartInfo(_executablePath)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (preventServerStart)
        {
            startInfo.ArgumentList.Add("-N");
        }

        foreach (string prefixArgument in _prefixArguments)
        {
            startInfo.ArgumentList.Add(prefixArgument);
        }

        foreach (string argument in encodedArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private async Task<byte[]> ReadBoundedAsync(Stream stream)
    {
        using var captured = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            if (read == 0)
            {
                return captured.ToArray();
            }

            if (captured.Length + read > _limits.MaxCapturedBytesPerStream)
            {
                throw new InvalidDataException(
                    $"A tmux output stream exceeded {_limits.MaxCapturedBytesPerStream} bytes.");
            }

            captured.Write(buffer, 0, read);
        }
    }

    private static async Task AwaitProcessAndPumpsAsync(
        CancellationToken cancellationToken,
        params Task[] tasks)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new List<Task>(tasks);
        var cancellationSignal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            cancellationSignal);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task completed = await Task.WhenAny([.. pending, cancellationSignal.Task])
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            pending.Remove(completed);
            try
            {
                await completed.ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                throw new OperationTaskException(completed, failure);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task CleanupAsync(
        ITmuxProcessHandle process,
        Exception primaryFailure,
        Task? primaryOperation,
        params Task?[] streamPumps)
    {
        Task kill = Task.Run(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
        });
        Task reap = Task.Run(
            async () => await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false));
        Task[] operations =
        [
            kill,
            reap,
            .. streamPumps.Where(static task => task is not null).Cast<Task>(),
        ];
        Task all = Task.WhenAll(operations);

        try
        {
            await all
                .WaitAsync(_limits.CleanupTimeout, _timeProvider, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException timeout) when (!all.IsCompleted)
        {
            ObserveFutureFailures([all, .. operations]);
            ThrowCleanupFailures(
                operations,
                primaryFailure,
                primaryOperation,
                boundaryFailure: timeout);
        }
        catch (Exception boundaryFailure)
        {
            if (!all.IsCompleted)
            {
                ObserveFutureFailures([all, .. operations]);
                ThrowCleanupFailures(
                    operations,
                    primaryFailure,
                    primaryOperation,
                    boundaryFailure);
            }
            else
            {
                ThrowCleanupFailures(operations, primaryFailure, primaryOperation);
            }
        }
    }

    private static void ThrowCleanupFailures(
        IEnumerable<Task> operations,
        Exception primaryFailure,
        Task? primaryOperation,
        Exception? boundaryFailure = null)
    {
        var primaryGraph = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        AddExceptionGraph(primaryFailure, primaryGraph);
        var failures = new List<Exception>();
        var observed = new HashSet<Exception>(
            primaryGraph,
            ReferenceEqualityComparer.Instance);
        var primaryCanceledTasks = new HashSet<Task>(ReferenceEqualityComparer.Instance);
        primaryCanceledTasks.UnionWith(
            primaryGraph
                .OfType<TaskCanceledException>()
                .Select(static failure => failure.Task)
                .Where(static task => task is not null)
                .Cast<Task>());
        if (primaryOperation is not null && primaryOperation.IsCanceled)
        {
            primaryCanceledTasks.Add(primaryOperation);
        }
        foreach (Task operation in operations)
        {
            if (operation.IsFaulted)
            {
                foreach (Exception failure in operation.Exception!.Flatten().InnerExceptions)
                {
                    if (observed.Add(failure))
                    {
                        failures.Add(failure);
                    }
                }
            }
            else if (operation.IsCanceled)
            {
                if (primaryCanceledTasks.Contains(operation))
                {
                    continue;
                }

                var failure = new TaskCanceledException(operation);
                if (observed.Add(failure))
                {
                    failures.Add(failure);
                }
            }
        }

        if (boundaryFailure is not null && observed.Add(boundaryFailure))
        {
            failures.Add(boundaryFailure);
        }

        if (failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            throw failures[0];
        }

        throw new AggregateException(failures);
    }

    private static void AddExceptionGraph(
        Exception failure,
        HashSet<Exception> graph)
    {
        if (!graph.Add(failure))
        {
            return;
        }

        if (failure is AggregateException aggregate)
        {
            foreach (Exception innerFailure in aggregate.InnerExceptions)
            {
                AddExceptionGraph(innerFailure, graph);
            }
        }
        else if (failure.InnerException is not null)
        {
            AddExceptionGraph(failure.InnerException, graph);
        }
    }

    private static void ObserveFutureFailures(IEnumerable<Task> operations)
    {
        foreach (Task operation in operations)
        {
            _ = operation.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously
                    | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    private sealed class OperationTaskException(Task operation, Exception failure)
        : Exception("A process lifecycle task failed.", failure)
    {
        internal Task Operation { get; } = operation;

        internal Exception Failure { get; } = failure;
    }

    private sealed class SystemProcessLauncher : ITmuxProcessLauncher
    {
        public ITmuxProcessHandle Start(ProcessStartInfo startInfo)
        {
            var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("The tmux client process did not start.");
                }

                return new SystemProcessHandle(process);
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
    }

    private sealed class DelegateProcessLauncher(Func<ProcessStartInfo, Process> launcher)
        : ITmuxProcessLauncher
    {
        public ITmuxProcessHandle Start(ProcessStartInfo startInfo)
        {
            Process process = launcher(startInfo);
            return new SystemProcessHandle(process);
        }
    }

    private sealed class SystemProcessHandle(Process process) : ITmuxProcessHandle
    {
        public int Id => process.Id;

        public Stream StandardOutput => process.StandardOutput.BaseStream;

        public Stream StandardError => process.StandardError.BaseStream;

        public int ExitCode => process.ExitCode;

        public bool HasExited => process.HasExited;

        public void Kill() => process.Kill(entireProcessTree: false);

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            process.WaitForExitAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
