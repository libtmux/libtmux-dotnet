using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LibTmux.Internal;

internal sealed class OwnedCleanup(Func<CancellationToken, Task> cleanup) : IAsyncDisposable
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private Task? _stopping;

    public ValueTask DisposeAsync()
    {
        while (true)
        {
            Task? current = Volatile.Read(ref _stopping);
            if (current is not null && !current.IsFaulted && !current.IsCanceled)
            {
                return new ValueTask(current);
            }

            var attempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _stopping, attempt.Task, current) == current)
            {
                _ = StopAsync(attempt);
                return new ValueTask(attempt.Task);
            }
        }
    }

    private async Task StopAsync(TaskCompletionSource attempt)
    {
        using CancellationTokenSource deadline = new(Timeout);
        try
        {
            await cleanup(deadline.Token).ConfigureAwait(false);
            attempt.SetResult();
        }
        catch (Exception error)
        {
            attempt.SetException(error);
        }
    }

    internal static async Task<TmuxCommandResult> DispatchCreationAsync(
        Func<Task<TmuxCommandResult>> dispatch, string command, CancellationToken deadline)
    {
        try
        {
            return await dispatch().ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            throw new TmuxTransportException(
                $"Creation command '{command}' did not return a receipt before its acquisition deadline. The remote outcome is unknown; inspect the endpoint before retrying.",
                [command], TmuxDispatchState.Unknown, error);
        }
    }

    [UnsupportedOSPlatform("windows")]
    internal static async Task DestroyAsync(Server server, ServerGeneration generation, string kind, string? id, CancellationToken token)
    {
        TmuxConnection connection = server.Connection
            ?? throw new InvalidOperationException("The owned handle has no connection identity.");
        if (connection.OwnershipToken is null)
        {
            throw new InvalidOperationException("Cleanup requires the daemon token captured at acquisition.");
        }
        string command = "kill-" + kind;
        TmuxCommandResult result = await connection.CreateEntityDispatcher(generation)
            .ExecuteAsync(id is null ? [command] : [command, "-t", id], token).ConfigureAwait(false);
        if (TmuxCommandFailure.NamesMissingServer(result) && ProcessIsRunning(generation.ProcessId))
        {
            throw new TmuxTransportException(
                "The endpoint is absent, but its captured process ID is still live. Destruction could not be verified; cleanup remains retryable.",
                result.Arguments, TmuxDispatchState.Unknown);
        }
        if (result.ExitCode != 0 && !TmuxCommandFailure.NamesMissingServer(result)
            && !result.StandardErrorLines.Any(line => line.StartsWith("can't find " + kind + ":", StringComparison.Ordinal)))
        {
            TmuxCommandFailure.ThrowIfFailed(result, command);
        }
    }

    private static bool ProcessIsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    [UnsupportedOSPlatform("windows")]
    internal static async Task<T> CompleteAcquisitionAsync<T>(
        Func<Task<T>> readback, Server server, ServerGeneration generation, string kind, string id)
    {
        try
        {
            return await readback().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Exception failure = error is OperationCanceledException ? error
                : new LibTmuxException(TmuxMutationSequence.PartialFailureMessage, TmuxDispatchState.Unknown, error);
            await RollbackAsync(failure, server, generation, kind, id).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    [UnsupportedOSPlatform("windows")]
    internal static async Task RollbackAsync(Exception failure, Server server, ServerGeneration generation, string kind, string id)
    {
        var owner = new OwnedCleanup(token => DestroyAsync(server, generation, kind, id, token));
        await OwnedScope.PreserveCleanupAsync(failure, owner).ConfigureAwait(false);
    }
}
