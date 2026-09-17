using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Server
{
    /// <summary>Builds the arguments a shell request sends.</summary>
    /// <remarks>
    /// Three of these flags arrived at different tmux versions, so this stays
    /// on the server that knows which one is answering rather than becoming a
    /// helper a caller could reach without that knowledge.
    /// </remarks>
    internal List<string> BuildRunShellArguments(RunShellRequest request)
    {
        List<string> arguments = ["run-shell"];
        ServerUtilities.AddFlag(arguments, request.Background, "-b");
        ServerUtilities.AddFlag(arguments, request.AsTmuxCommand, "-C");
        if (request.ShowStandardError
            && RequiresCapability(
                ServerUtilities.RunShellStandardErrorCapability,
                LogRunShellStandardError))
        {
            arguments.Add("-E");
        }

        if (request.WorkingDirectory is not null
            && RequiresCapability(
                ServerUtilities.RunShellWorkingDirectoryCapability,
                LogRunShellWorkingDirectory))
        {
            ServerUtilities.AddValue(arguments, "-c", request.WorkingDirectory);
        }

        ServerUtilities.AddValue(
            arguments,
            "-d",
            request.Delay is TimeSpan delay
                ? ((long)delay.TotalSeconds).ToString(CultureInfo.InvariantCulture)
                : null);
        ServerUtilities.AddValue(arguments, "-t", request.TargetPane);
        arguments.Add(request.Command);
        if (request.Arguments is { Count: > 0 } extra
            && RequiresCapability(
                ServerUtilities.RunShellArgumentsCapability,
                LogRunShellArguments))
        {
            arguments.AddRange(extra);
        }

        return arguments;
    }

    /// <summary>Runs a shell command and reports what it printed.</summary>
    /// <param name="request">What to run, and how.</param>
    /// <param name="cancellationToken">
    /// Cancels waiting for tmux's reply. tmux itself is not told: the server,
    /// not this process, owns <c>run-shell</c>'s spawned command, so cancelling
    /// stops this call from waiting on it, not the command from running. Nor
    /// does stopping the whole server: <see cref="KillAsync" /> does not reap
    /// a <c>run-shell</c> child either, so it keeps running as its own
    /// orphaned process.
    /// </param>
    /// <returns>What the command printed, or null when tmux did not wait for it.</returns>
    /// <remarks>
    /// The directory flag arrived in tmux 3.4, the error-output flag in 3.6,
    /// and passing arguments without a shell in 3.7.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>?> RunShellAsync(
        RunShellRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildRunShellArguments(request);

        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "run-shell");

        // Nothing has run yet when tmux was told not to wait, so there is
        // nothing it could report.
        return request.Background ? null : result.StandardOutputLines;
    }

    /// <summary>Runs one tmux command or another depending on a shell command.</summary>
    /// <param name="request">What to test, and what to run either way.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task IfShellAsync(IfShellRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunUtilityAsync(BuildIfShellArguments(request), cancellationToken);
    }

    internal static List<string> BuildIfShellArguments(IfShellRequest request)
    {
        List<string> arguments = ["if-shell"];
        ServerUtilities.AddFlag(arguments, request.Background, "-b");
        ServerUtilities.AddValue(arguments, "-t", request.TargetPane);
        arguments.Add(request.ShellCommand);
        arguments.Add(string.Join(' ', request.ThenCommand));
        if (request.ElseCommand is { Count: > 0 } otherwise)
        {
            arguments.Add(string.Join(' ', otherwise));
        }

        return arguments;
    }

    /// <summary>Waits on, signals, locks, or unlocks a tmux channel.</summary>
    /// <param name="request">Which channel, and what to do with it.</param>
    /// <param name="cancellationToken">
    /// Cancels waiting for tmux's reply. For <see cref="TmuxWaitMode.Wait" />
    /// this kills the client while tmux keeps its queue entry, eating the next
    /// real signal — prefer <see cref="OpenWaitChannel" /> whenever a
    /// <see cref="TmuxWaitMode.Wait" /> needs a deadline. For
    /// <see cref="TmuxWaitMode.Lock" /> cancelling never kills the client, since
    /// tmux hands a released lock to whichever queued client is still alive: the
    /// client keeps running, and a lock it goes on to acquire is released again
    /// automatically once this call has already given up on it.
    /// </param>
    /// <remarks>
    /// Waiting blocks until something else signals the channel, so a call that
    /// waits does not return on its own.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task WaitForAsync(WaitForRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Mode == TmuxWaitMode.Lock
            ? WaitForLockAsync(request, cancellationToken)
            : RunUtilityAsync(BuildWaitForArguments(request), cancellationToken);
    }

    // A killed locker leaves its queue entry behind, and tmux hands the lock
    // to it forever, so a Lock request is never cancelled once dispatched --
    // only this attempt's own wait for it is. A lock the abandoned request
    // goes on to acquire is released again here instead.
    [UnsupportedOSPlatform("windows")]
    private async Task WaitForLockAsync(WaitForRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> arguments = BuildWaitForArguments(request);
        Task<TmuxCommandResult> locking = _commandDispatcher.ExecuteAsync(arguments, CancellationToken.None);
        try
        {
            TmuxCommandResult result = await locking.WaitAsync(cancellationToken).ConfigureAwait(false);
            TmuxCommandFailure.ThrowIfFailed(result, arguments[0]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ReleaseIfAcquiredAsync(locking, request.Channel);
            throw;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private async Task ReleaseIfAcquiredAsync(Task<TmuxCommandResult> locking, string channel)
    {
        TmuxCommandResult result;
        try
        {
            result = await locking.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The lock request never dispatched or the server rejected it, so
            // there is nothing this abandoned caller left holding.
            return;
        }

        if (result.ExitCode != 0 || result.StandardErrorLines.Count > 0)
        {
            return;
        }

        try
        {
            await RunUtilityAsync(
                    BuildWaitForArguments(new WaitForRequest(channel, TmuxWaitMode.Unlock)),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TmuxCommandException)
        {
            // Best effort: the server may be gone, or another release already
            // cleared it.
        }
    }

    /// <summary>Opens a wait on a channel that survives a timed attempt.</summary>
    /// <param name="channel">The channel to wait on.</param>
    /// <returns>The open wait, which must be disposed to withdraw it.</returns>
    /// <remarks>
    /// Prefer this to <see cref="WaitForAsync" /> whenever the wait has a
    /// deadline. Cancelling a waiting <c>wait-for</c> kills its client while
    /// tmux keeps the registration, and that registration eats the next signal.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public TmuxWaitChannel OpenWaitChannel(string channel) => new(this, channel);

    internal static List<string> BuildWaitForArguments(WaitForRequest request)
    {
        List<string> arguments = ["wait-for"];
        if (ServerUtilities.GetWaitModeFlag(request.Mode) is string flag)
        {
            arguments.Add(flag);
        }

        arguments.Add(request.Channel);
        return arguments;
    }
}
