using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Pane
{
    /// <summary>Runs a shell command in this pane and reports its exit status.</summary>
    /// <param name="command">The command to run in a POSIX-compatible shell.</param>
    /// <param name="timeout">The time allowed for completion; defaults to thirty seconds and cannot exceed one day.</param>
    /// <param name="suppressHistory">Prefixes the shell input with a space for shells that ignore such history entries.</param>
    /// <param name="cancellationToken">Cancels the wait for completion.</param>
    /// <returns>An authenticated exit status, or a timed-out result whose command may still be running.</returns>
    /// <remarks>
    /// <para>The command runs in a subshell, so changes to its working directory and environment do not persist.</para>
    /// <para>This method does not capture output. Read the pane separately when rendered text is needed; pane text is not a byte-exact stdout or stderr stream.</para>
    /// <para>Cancellation after dispatch may leave the command running. Do not automatically retry after an uncertain dispatch or timeout. Runs to the same server generation and pane are reserved within this process; callers in other processes are not coordinated.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The command is empty, contains NUL, or exceeds the input limit.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive or exceeds one day.</exception>
    /// <exception cref="LibTmuxException">The pane is not a single writable shell, the route changed, or the command result could not be authenticated.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<PaneCommandResult> RunCommandAsync(
        string command,
        TimeSpan? timeout = null,
        bool suppressHistory = true,
        CancellationToken cancellationToken = default)
    {
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout,
                "The pane command timeout must be positive and no longer than one day.");
        }

        PaneCommandObservation run = await PaneCommandRunner.RunAsync(
                this,
                command,
                effectiveTimeout,
                suppressHistory,
                maximumBytes: 64 * 1024,
                markerLifetime: effectiveTimeout + TimeSpan.FromMinutes(1),
                dispatchPreflight: null,
                noteDispatch: null,
                onTokenCreated: null,
                onRetentionStarted: null,
                onRunReleased: null,
                reportProgress: null,
                cancellationToken)
            .ConfigureAwait(false);
        return run.Result;
    }

    [UnsupportedOSPlatform("windows")]
    internal Task<PaneCommandObservation> RunCommandCoreAsync(
        string command,
        TimeSpan timeout,
        TimeSpan markerLifetime,
        bool suppressHistory,
        int maximumBytes,
        Func<CancellationToken, Task<Pane>> dispatchPreflight,
        Func<Pane, string, Action<bool>> noteDispatch,
        Action<PaneCommandRunner.RunToken> onTokenCreated,
        Action onRetentionStarted,
        Action onRunReleased,
        Action<TimeSpan>? reportProgress,
        CancellationToken cancellationToken) =>
        PaneCommandRunner.RunAsync(
            this,
            command,
            timeout,
            suppressHistory,
            maximumBytes,
            markerLifetime,
            dispatchPreflight,
            noteDispatch,
            onTokenCreated,
            onRetentionStarted,
            onRunReleased,
            reportProgress,
            cancellationToken);
}

/// <summary>Reports the authenticated outcome of a command sent to one pane.</summary>
/// <param name="PaneId">The pane that received the command.</param>
/// <param name="ExitStatus">The shell exit status when completion was authenticated; otherwise null.</param>
/// <param name="TimedOut">Whether the completion wait expired while the command may still be running.</param>
/// <param name="Elapsed">The time spent staging and waiting for the command.</param>
/// <param name="EffectiveTimeout">The timeout applied to this run.</param>
public sealed record PaneCommandResult(
    PaneId PaneId,
    int? ExitStatus,
    bool TimedOut,
    TimeSpan Elapsed,
    TimeSpan EffectiveTimeout);
