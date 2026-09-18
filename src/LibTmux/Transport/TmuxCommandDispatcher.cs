using System.Diagnostics;
using System.Runtime.Versioning;

namespace LibTmux.Internal;

internal sealed class TmuxCommandDispatcher
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<TmuxCommandResult>> _execute;

    // Grouping needs a real transport: tmux splits the commands itself, and a
    // joined semicolon would just be data. A stub executor leaves this null.
    private readonly Func<
        IReadOnlyList<IReadOnlyList<string>>,
        CancellationToken,
        Task<TmuxCommandResult>>? _executeGroup;

    private readonly TmuxCommandContext? _context;

    [UnsupportedOSPlatform("windows")]
    internal TmuxCommandDispatcher(TmuxProcessTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _execute = transport.ExecuteAsync;
        _executeGroup = (commands, cancellationToken) => transport.ExecuteAsync(
            TmuxCommandRequest.Group([.. commands]),
            cancellationToken);
    }

    internal TmuxCommandDispatcher(
        Func<IReadOnlyList<string>, CancellationToken, Task<TmuxCommandResult>> execute,
        TmuxCommandContext? context = null,
        Func<
            IReadOnlyList<IReadOnlyList<string>>,
            CancellationToken,
            Task<TmuxCommandResult>>? executeGroup = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _executeGroup = executeGroup;
        _context = context;
    }

    [UnsupportedOSPlatform("windows")]
    internal async Task<TmuxCommandResult> ExecuteGroupAsync(
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (_executeGroup is null)
        {
            throw new NotSupportedException(
                "This dispatcher cannot run a grouped command.");
        }

        foreach (IReadOnlyList<string> command in commands)
        {
            ValidateArguments(command);
        }

        // A group is one tmux run, so it is recorded once, under the arguments
        // tmux actually received.
        string[] flattened = [.. commands.SelectMany(static c => c)];
        string? socket = _context?.Socket;
        using Activity? activity = TmuxInstrumentation.StartCommand(flattened, socket);
        long started = Stopwatch.GetTimestamp();
        TmuxCommandResult result;
        try
        {
            result = await _executeGroup(commands, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            TmuxInstrumentation.Fail(activity, started, flattened, socket, error);
            throw;
        }

        TmuxInstrumentation.Complete(activity, started, flattened, socket, result.ExitCode);
        TmuxLog.CommandCompleted(_context, flattened, result);
        return result;
    }

    [UnsupportedOSPlatform("windows")]
    internal async Task<TmuxCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(arguments);
        string[] copy = [.. arguments];
        string? socket = _context?.Socket;
        using Activity? activity = TmuxInstrumentation.StartCommand(copy, socket);
        long started = Stopwatch.GetTimestamp();
        TmuxCommandResult result;
        try
        {
            result = await _execute(copy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            TmuxInstrumentation.Fail(activity, started, copy, socket, error);
            throw;
        }

        TmuxInstrumentation.Complete(activity, started, copy, socket, result.ExitCode);
        TmuxLog.CommandCompleted(_context, copy, result);

        if (copy.Contains("has-session", StringComparer.Ordinal)
            && result.StandardOutputLines.Count == 0
            && result.StandardErrorLines.Count > 0)
        {
            return new TmuxCommandResult(
                result.Arguments,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError,
                [result.StandardErrorLines[0]],
                result.StandardErrorLines);
        }

        return result;
    }

    internal static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            throw new ArgumentException("At least one tmux argument is required.", nameof(arguments));
        }

        if (arguments.Any(static argument => argument is null))
        {
            throw new ArgumentException("Tmux arguments cannot be null.", nameof(arguments));
        }
    }
}
