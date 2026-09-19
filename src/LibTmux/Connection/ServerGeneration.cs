using System.Globalization;

namespace LibTmux;

/// <summary>Identifies one tmux daemon generation.</summary>
public readonly record struct ServerGeneration
{
    /// <summary>
    /// The tmux format string that reports a generation as
    /// <c>ProcessId:StartTime</c>, the shape <see cref="Parse(string)" /> reads.
    /// </summary>
    public const string DisplayFormat = "#{pid}:#{start_time}";

    /// <summary>Initializes a server generation.</summary>
    public ServerGeneration(int processId, long startTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startTime);
        ProcessId = processId;
        StartTime = startTime;
    }

    /// <summary>Gets the tmux daemon process identifier.</summary>
    public int ProcessId { get; }

    /// <summary>Gets the tmux daemon start time.</summary>
    public long StartTime { get; }

    /// <summary>Parses a generation reported in <see cref="DisplayFormat" />.</summary>
    /// <param name="text">The text tmux reported.</param>
    /// <returns>The parsed generation.</returns>
    /// <exception cref="TmuxProtocolException">
    /// <paramref name="text" /> is not a colon-separated pair of positive
    /// integers.
    /// </exception>
    public static ServerGeneration Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] fields = text.Split(':');
        if (fields.Length != 2
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
            || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long startTime))
        {
            throw new TmuxProtocolException("tmux reported a malformed server generation.", TmuxDispatchState.Dispatched);
        }

        try
        {
            return new ServerGeneration(processId, startTime);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new TmuxProtocolException("tmux reported a nonpositive server generation.", TmuxDispatchState.Dispatched, error);
        }
    }
}
