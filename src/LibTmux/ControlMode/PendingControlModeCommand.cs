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

    private TmuxCommand Command { get; } = command;

    private List<string> ErrorLines { get; } = [];

    private List<string> OutputLines { get; } = [];

    internal string Sentinel { get; } = sentinel;

    internal TaskCompletionSource<IReadOnlyList<string>> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Enqueued => _enqueued.Task;

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
            _replyBlocks++;
            if (_replyBlocks > limits.MaxReplyBlocks)
            {
                throw new InvalidDataException(
                    $"A control-mode reply exceeded its {limits.MaxReplyBlocks}-block limit.");
            }

            if (lines.Count > limits.MaxReplyLines - _replyLines)
            {
                throw new InvalidDataException(
                    $"A control-mode reply exceeded its {limits.MaxReplyLines}-line limit.");
            }

            if (blockBytes > limits.MaxReplyBytes - _replyBytes)
            {
                throw new InvalidDataException(
                    $"A control-mode reply exceeded its {limits.MaxReplyBytes}-byte limit.");
            }

            _replyLines += lines.Count;
            _replyBytes += blockBytes;
            if (_abandoned)
            {
                return;
            }

            _failed |= failed;
            (failed ? ErrorLines : OutputLines).AddRange(lines);
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
}
