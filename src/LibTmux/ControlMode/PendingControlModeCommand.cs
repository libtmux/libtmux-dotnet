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
    private string? _overflow;

    internal TmuxCommand Command { get; } = command;

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
            if (!ReserveReply(lines.Count, blockBytes, limits) || _abandoned)
            {
                return;
            }

            _failed |= failed;
            (failed ? ErrorLines : OutputLines).AddRange(lines);
        }
    }

    /// <summary>Records that a block of the reply was dropped for its size.</summary>
    /// <param name="reason">Which limit it passed.</param>
    internal void Overflow(string reason)
    {
        lock (_gate)
        {
            _overflow ??= reason;
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_overflow is not null && !_abandoned)
            {
                Completion.TrySetException(new ControlModeReplyLimitException(_overflow));
                return;
            }

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

    // Returns false when the reply passed a limit and the session drops it
    // rather than ending; the command then fails with the reason.
    private bool ReserveReply(
        int lineCount,
        int bytes,
        ControlModeLimits limits)
    {
        string? exceeded = null;
        if (_overflow is not null)
        {
            return false;
        }

        if (++_replyBlocks > limits.MaxReplyBlocks)
        {
            exceeded = $"A control-mode reply exceeded its {limits.MaxReplyBlocks}-block limit.";
        }
        else if (lineCount > limits.MaxReplyLines - _replyLines)
        {
            exceeded = $"A control-mode reply exceeded its {limits.MaxReplyLines}-line limit.";
        }
        else if (bytes > limits.MaxReplyBytes - _replyBytes)
        {
            exceeded = $"A control-mode reply exceeded its {limits.MaxReplyBytes}-byte limit.";
        }

        if (exceeded is not null)
        {
            if (!limits.FailOnlyOversizedCommand)
            {
                throw new TmuxProtocolException(exceeded, TmuxDispatchState.Unknown);
            }

            _overflow = exceeded;
            return false;
        }

        _replyLines += lineCount;
        _replyBytes += bytes;
        return true;
    }
}

/// <summary>A command's reply passed a limit; the session dropped it and went on.</summary>
internal sealed class ControlModeReplyLimitException(string reason) : Exception(reason);

/// <summary>Reports whether a request sent through <c>SendRawAsync</c> reached the point of being written.</summary>
internal sealed class ControlModeSendProbe
{
    internal PendingControlModeCommand? Pending { get; set; }

    /// <summary>Gets whether tmux may have received the request.</summary>
    /// <remarks>A request that never reached the write cannot have run, so it is safe to send again.</remarks>
    internal bool MayHaveRun => Pending is { Enqueued.IsCompleted: true };
}
