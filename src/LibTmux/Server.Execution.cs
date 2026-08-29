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
    /// <param name="cancellationToken">Cancels the tmux command.</param>
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

    /// <summary>Waits on, signals, or locks a tmux channel.</summary>
    /// <param name="request">Which channel, and what to do with it.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// Waiting blocks until something else signals the channel, so a call that
    /// waits does not return on its own.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task WaitForAsync(WaitForRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunUtilityAsync(BuildWaitForArguments(request), cancellationToken);
    }

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
