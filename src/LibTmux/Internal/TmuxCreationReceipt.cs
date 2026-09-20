using System.Globalization;

namespace LibTmux.Internal;

internal readonly record struct TmuxCreationReceipt(
    ServerGeneration Generation,
    SessionId SessionId,
    WindowId WindowId,
    PaneId PaneId,
    int WindowIndex)
{
    internal const string Format = TmuxConnection.GenerationFormat + "\t#{session_id}\t#{window_id}\t#{pane_id}\t#{window_index}";

    internal static TmuxCreationReceipt Parse(TmuxCommandResult result)
    {
        if (result.StandardOutputLines.Count != 1)
        {
            throw new TmuxCommandException("tmux did not report exactly one creation receipt.", result);
        }

        string[] fields = result.StandardOutputLines[0].Split('\t');
        if (fields.Length != 5
            || !SessionId.TryParse(fields[1], out SessionId sessionId)
            || !WindowId.TryParse(fields[2], out WindowId windowId)
            || !PaneId.TryParse(fields[3], out PaneId paneId)
            || !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out int windowIndex))
        {
            throw new TmuxCommandException("tmux reported a malformed creation receipt.", result);
        }

        return new TmuxCreationReceipt(TmuxConnection.ParseGeneration(fields[0]), sessionId, windowId, paneId, windowIndex);
    }
}
