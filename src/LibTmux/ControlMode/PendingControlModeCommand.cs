using System.Globalization;
using System.Text;

namespace LibTmux;

internal sealed class PendingControlModeCommand(TmuxCommand command, string sentinel)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _enqueued =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _abandoned;
    private bool _failed;
    private int _replyBlocks;
    private int _replyBytes;
    private int _replyLines;

    private string? DeferredShellCommand { get; } = GetForegroundRunShellCommand(command);

    private TmuxCommand Command { get; } = command;

    private List<string> ErrorLines { get; } = [];

    private List<string> OutputLines { get; } = [];

    internal string Sentinel { get; } = sentinel;

    internal TaskCompletionSource<IReadOnlyList<string>> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Enqueued => _enqueued.Task;

    internal bool AcceptsDeferredShellOutput => DeferredShellCommand is not null;

    internal void MarkEnqueued() => _enqueued.TrySetResult();

    internal void Abandon()
    {
        lock (_gate)
        {
            _abandoned = true;
            _failed = false;
            ErrorLines.Clear();
            OutputLines.Clear();
        }
    }

    internal void AddBlock(
        List<string> lines,
        int blockBytes,
        bool failed,
        ControlModeLimits limits)
    {
        lock (_gate)
        {
            ReserveReply(lines.Count, blockBytes, isBlock: true, limits);
            if (_abandoned)
            {
                return;
            }

            _failed |= failed;
            (failed ? ErrorLines : OutputLines).AddRange(lines);
        }
    }

    internal void AddDeferredShellOutput(string line, ControlModeLimits limits)
    {
        lock (_gate)
        {
            ReserveReply(
                lineCount: 1,
                Encoding.UTF8.GetByteCount(line),
                isBlock: false,
                limits);
            if (_abandoned)
            {
                return;
            }

            bool failed = IsDeferredShellFailure(line);
            (failed ? ErrorLines : OutputLines).Add(line);
            _failed |= failed;
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (!_failed)
            {
                Completion.TrySetResult([.. OutputLines]);
                return;
            }

            string reported = string.Join('\n', ErrorLines);
            Completion.TrySetException(new ControlModeCommandException(
                reported.Length == 0 ? "The tmux command failed." : reported,
                Command,
                OutputLines,
                ErrorLines));
        }
    }

    private void ReserveReply(
        int lineCount,
        int bytes,
        bool isBlock,
        ControlModeLimits limits)
    {
        if (isBlock && ++_replyBlocks > limits.MaxReplyBlocks)
        {
            throw new TmuxProtocolException(
                $"A control-mode reply exceeded its {limits.MaxReplyBlocks}-block limit.",
                TmuxDispatchState.Unknown);
        }

        if (lineCount > limits.MaxReplyLines - _replyLines)
        {
            throw new TmuxProtocolException(
                $"A control-mode reply exceeded its {limits.MaxReplyLines}-line limit.",
                TmuxDispatchState.Unknown);
        }

        if (bytes > limits.MaxReplyBytes - _replyBytes)
        {
            throw new TmuxProtocolException(
                $"A control-mode reply exceeded its {limits.MaxReplyBytes}-byte limit.",
                TmuxDispatchState.Unknown);
        }

        _replyLines += lineCount;
        _replyBytes += bytes;
    }

    private bool IsDeferredShellFailure(string line)
    {
        string? shell = DeferredShellCommand;
        if (shell is null)
        {
            return false;
        }

        return IsDeferredShellFailure(line, $"'{shell}' returned ")
            || IsDeferredShellFailure(line, $"'{shell}' terminated by signal ");
    }

    private static bool IsDeferredShellFailure(string line, string prefix) =>
        line.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(
            line.AsSpan(prefix.Length),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int value)
        && value != 0;

    private static string? GetForegroundRunShellCommand(TmuxCommand command)
    {
        if (!string.Equals(command.Name, "run-shell", StringComparison.Ordinal)
            && !string.Equals(command.Name, "run", StringComparison.Ordinal))
        {
            return null;
        }

        IReadOnlyList<string> arguments = command.Arguments;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {
                return index + 1 < arguments.Count ? arguments[index + 1] : null;
            }

            if (argument.Length < 2 || argument[0] != '-')
            {
                return argument;
            }

            for (int flagIndex = 1; flagIndex < argument.Length; flagIndex++)
            {
                switch (argument[flagIndex])
                {
                    case 'b':
                        return null;
                    case 'C':
                    case 'E':
                        break;
                    case 'c':
                    case 'd':
                    case 't':
                        if (flagIndex != argument.Length - 1 || ++index >= arguments.Count)
                        {
                            return null;
                        }

                        break;
                    default:
                        return null;
                }
            }
        }

        return null;
    }
}
