using System.Runtime.ExceptionServices;

namespace LibTmux;

internal sealed class ControlModeDisposer
{
    private readonly TimeSpan _budget;
    private readonly Task _outputPump;
    private readonly IControlModeProcess _process;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock;

    internal ControlModeDisposer(
        IControlModeProcess process,
        SemaphoreSlim writeLock,
        Task outputPump,
        TimeSpan budget,
        TimeProvider timeProvider)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _writeLock = writeLock ?? throw new ArgumentNullException(nameof(writeLock));
        _outputPump = outputPump ?? throw new ArgumentNullException(nameof(outputPump));
        _budget = budget;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal async Task DisposeAsync()
    {
        var cleanupFailures = new List<Exception>();
        using var boundaryCancellation = new CancellationTokenSource();
        Exception? boundarySetupFailure = null;
        Task deadlineBoundary;
        Task graceBoundary;
        try
        {
            deadlineBoundary = Task.Delay(
                _budget,
                _timeProvider,
                boundaryCancellation.Token);
            graceBoundary = Task.Delay(
                TimeSpan.FromTicks(_budget.Ticks / 2),
                _timeProvider,
                boundaryCancellation.Token);
        }
        catch (Exception error)
        {
            boundarySetupFailure = error;
            deadlineBoundary = Task.CompletedTask;
            graceBoundary = Task.CompletedTask;
        }

        bool writeLockHeld = false;
        Task? writeLockWait = null;
        Task exitWait = Task.CompletedTask;
        Task errorPumpStop = Task.CompletedTask;
        Task[] operations = [];
        Task all = Task.CompletedTask;
        bool deadlineExceeded = false;
        try
        {
            writeLockHeld = _writeLock.Wait(0, CancellationToken.None);
            if (!writeLockHeld)
            {
                writeLockWait = _writeLock.WaitAsync(CancellationToken.None);
                if (await CompletesBeforeAsync(writeLockWait, graceBoundary)
                    .ConfigureAwait(false))
                {
                    if (writeLockWait.IsCompletedSuccessfully)
                    {
                        writeLockHeld = true;
                    }
                    else
                    {
                        AddTaskFailures(writeLockWait, cleanupFailures);
                    }

                    writeLockWait = null;
                }
            }

            exitWait = await BeginProcessStopAsync(
                    cleanupFailures,
                    forceStop: !writeLockHeld,
                    graceBoundary)
                .ConfigureAwait(false);
            errorPumpStop = StartErrorPumpStop();
            operations = writeLockWait is null
                ? [exitWait, _outputPump, errorPumpStop]
                : [exitWait, _outputPump, errorPumpStop, writeLockWait!];
            all = Task.WhenAll(operations);
            deadlineExceeded = !await CompletesBeforeAsync(all, deadlineBoundary)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
        }
        finally
        {
            if (!all.IsCompleted)
            {
                ObserveFutureFailures([all, .. operations]);
            }

            try
            {
                boundaryCancellation.Cancel();
            }
            catch (Exception error)
            {
                cleanupFailures.Add(error);
            }

            try
            {
                _process.Dispose();
            }
            catch (Exception error)
            {
                cleanupFailures.Add(error);
            }

            if (!writeLockHeld && writeLockWait?.IsCompletedSuccessfully == true)
            {
                writeLockHeld = true;
            }

            DisposeWriteLock(writeLockHeld, cleanupFailures);
        }

        Exception? pumpFailure = GetTaskFailure(_outputPump);
        foreach (Task operation in operations)
        {
            if (!ReferenceEquals(operation, _outputPump))
            {
                AddTaskFailures(operation, cleanupFailures);
            }
        }

        if (all.IsFaulted)
        {
            _ = all.Exception;
        }

        if (boundarySetupFailure is not null)
        {
            cleanupFailures.Add(boundarySetupFailure);
        }
        else if (deadlineExceeded)
        {
            cleanupFailures.Add(new TimeoutException(
                "Control-mode asynchronous cleanup exceeded its disposal deadline."));
        }

        ThrowFailures(pumpFailure, cleanupFailures);
    }

    private async Task<Task> BeginProcessStopAsync(
        List<Exception> cleanupFailures,
        bool forceStop,
        Task graceBoundary)
    {
        if (ReadHasExited(cleanupFailures))
        {
            return Task.CompletedTask;
        }

        Task exitWait = StartExitWait();
        bool exitFailureRecorded = false;
        if (!forceStop)
        {
            try
            {
                _process.CloseInput();
            }
            catch (InvalidOperationException) when (ProcessHasExited())
            {
                return Task.CompletedTask;
            }
            catch (Exception error)
            {
                cleanupFailures.Add(error);
            }

            if (await CompletesBeforeAsync(exitWait, graceBoundary).ConfigureAwait(false))
            {
                if (exitWait.IsCompletedSuccessfully)
                {
                    return exitWait;
                }

                if (IsBenignExitFailure(exitWait))
                {
                    return Task.CompletedTask;
                }

                AddTaskFailures(exitWait, cleanupFailures);
                exitFailureRecorded = true;
            }

            forceStop = true;
        }

        if (forceStop)
        {
            // Kill only the client; its server may still be serving other clients.
            try
            {
                if (!ProcessHasExited())
                {
                    _process.Kill();
                }
            }
            catch (InvalidOperationException) when (ProcessHasExited())
            {
            }
            catch (Exception error)
            {
                cleanupFailures.Add(error);
            }
        }

        if (!exitWait.IsCompleted || exitWait.IsCompletedSuccessfully)
        {
            return exitWait;
        }

        if (!exitFailureRecorded && !IsBenignExitFailure(exitWait))
        {
            AddTaskFailures(exitWait, cleanupFailures);
        }

        return ProcessHasExited() ? Task.CompletedTask : StartExitWait();
    }

    private bool ReadHasExited(List<Exception> cleanupFailures)
    {
        try
        {
            return _process.HasExited;
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
            return false;
        }
    }

    private bool ProcessHasExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private bool IsBenignExitFailure(Task exitWait) =>
        exitWait.IsFaulted
        && exitWait.Exception!.Flatten().InnerExceptions.All(
            static error => error is InvalidOperationException)
        && ProcessHasExited();

    private Task StartExitWait()
    {
        try
        {
            return _process.WaitForExitAsync(CancellationToken.None);
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    private Task StartErrorPumpStop()
    {
        try
        {
            return _process.StopErrorPumpAsync(CancellationToken.None);
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    private static async Task<bool> CompletesBeforeAsync(Task operation, Task boundary)
    {
        if (!operation.IsCompleted)
        {
            await Task.WhenAny(operation, boundary).ConfigureAwait(false);
        }

        return operation.IsCompleted;
    }

    private void DisposeWriteLock(bool writeLockHeld, List<Exception> cleanupFailures)
    {
        if (!writeLockHeld)
        {
            return;
        }

        try
        {
            _writeLock.Release();
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
        }

        try
        {
            _writeLock.Dispose();
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
        }
    }

    private static Exception? GetTaskFailure(Task operation)
    {
        if (operation.IsFaulted)
        {
            var failures = operation.Exception!
                .Flatten()
                .InnerExceptions;
            return failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }

        return operation.IsCanceled ? new TaskCanceledException(operation) : null;
    }

    private static void AddTaskFailures(Task operation, List<Exception> failures)
    {
        Exception? failure = GetTaskFailure(operation);
        if (failure is not null)
        {
            failures.Add(failure);
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

    private static void ThrowFailures(
        Exception? pumpFailure,
        List<Exception> cleanupFailures)
    {
        if (pumpFailure is not null)
        {
            if (cleanupFailures.Count == 0)
            {
                ExceptionDispatchInfo.Capture(pumpFailure).Throw();
            }

            throw new AggregateException([pumpFailure, .. cleanupFailures]);
        }

        if (cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        }

        if (cleanupFailures.Count > 1)
        {
            throw new AggregateException(cleanupFailures);
        }
    }
}
