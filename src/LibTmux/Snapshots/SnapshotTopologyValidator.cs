using System.Globalization;

namespace LibTmux;

// Counts are per winlink: global window and pane IDs repeat across placements.
internal static class SnapshotTopologyValidator
{
    internal static void Validate(
        SnapshotDepth depth,
        ServerGeneration generation,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> sessionRows,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> windowRows,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> paneRows,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessions = new Dictionary<SessionId, int>();
        foreach (IReadOnlyDictionary<string, string?> row in sessionRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SessionId id = ReadSession(row);
            int count = depth >= SnapshotDepth.Windows ? ReadNumber(row, "session_windows") : 0;
            if (!sessions.TryAdd(id, count))
            {
                throw Inconsistent($"Session '{id}' appears more than once in the capture.");
            }
        }
        if (depth < SnapshotDepth.Windows)
        {
            return;
        }

        var placements = new HashSet<(SessionId Session, int Index)>();
        var expectedPanes = new Dictionary<WindowEntityKey, int>();
        var windowCounts = new Dictionary<SessionId, int>();
        foreach (IReadOnlyDictionary<string, string?> row in windowRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowEntityKey key = ReadWindow(row);
            if (!sessions.ContainsKey(key.SessionId))
            {
                throw Inconsistent($"Window placement '{key}' names a session absent from the capture.");
            }
            if (!placements.Add((key.SessionId, key.WindowIndex)))
            {
                throw Inconsistent($"Session '{key.SessionId}' has duplicate window index '{key.WindowIndex}' in the capture.");
            }
            windowCounts[key.SessionId] = windowCounts.GetValueOrDefault(key.SessionId) + 1;
            expectedPanes.Add(key, depth >= SnapshotDepth.Panes ? ReadNumber(row, "window_panes") : 0);
        }
        foreach ((SessionId id, int expected) in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int actual = windowCounts.GetValueOrDefault(id);
            if (actual != expected)
            {
                throw Inconsistent($"Session '{id}' reported {expected} window links, but the capture acquired {actual}.");
            }
        }
        if (depth < SnapshotDepth.Panes)
        {
            return;
        }

        var paneSets = new Dictionary<WindowEntityKey, Dictionary<PaneId, int>>();
        var paneIndexes = new Dictionary<WindowEntityKey, HashSet<int>>();
        var paneOwners = new Dictionary<PaneId, (WindowId Window, int Index)>();
        foreach (WindowEntityKey key in expectedPanes.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            paneSets.Add(key, []);
            paneIndexes.Add(key, []);
        }
        foreach (IReadOnlyDictionary<string, string?> row in paneRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowEntityKey key = ReadWindow(row);
            if (!PaneId.TryParse(Read(row, "pane_id"), out PaneId pane))
            {
                throw new TmuxProtocolException("tmux reported a malformed pane identifier.", TmuxDispatchState.Dispatched);
            }
            int index = ReadNumber(row, "pane_index");
            if (!paneSets.TryGetValue(key, out Dictionary<PaneId, int>? members))
            {
                throw Inconsistent($"Pane '{pane}' names window placement '{key}' absent from the capture.");
            }
            if (!members.TryAdd(pane, index) || !paneIndexes[key].Add(index))
            {
                throw Inconsistent($"Window placement '{key}' has duplicate pane '{pane}' or pane index '{index}' in the capture.");
            }
            var owner = (key.WindowId, index);
            if (paneOwners.TryGetValue(pane, out (WindowId Window, int Index) previous) && previous != owner)
            {
                throw Inconsistent($"Pane '{pane}' has conflicting window or pane index assignments in the capture.");
            }
            paneOwners[pane] = owner;
        }

        var physicalWindows = new Dictionary<WindowId, Dictionary<PaneId, int>>();
        foreach ((WindowEntityKey key, int expected) in expectedPanes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<PaneId, int> members = paneSets[key];
            if (members.Count != expected)
            {
                throw Inconsistent($"Window placement '{key}' reported {expected} panes, but the capture acquired {members.Count}.");
            }
            if (physicalWindows.TryGetValue(key.WindowId, out Dictionary<PaneId, int>? previous))
            {
                if (previous.Count != members.Count)
                {
                    throw Inconsistent($"Window '{key.WindowId}' has different pane sets across captured placements.");
                }
                foreach ((PaneId pane, int index) in members)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!previous.TryGetValue(pane, out int previousIndex) || previousIndex != index)
                    {
                        throw Inconsistent($"Window '{key.WindowId}' has different pane sets across captured placements.");
                    }
                }
            }
            else
            {
                physicalWindows.Add(key.WindowId, members);
            }
        }

        InconsistentSnapshotException Inconsistent(string message) => new(message, depth, generation);
    }

    private static WindowEntityKey ReadWindow(IReadOnlyDictionary<string, string?> row)
    {
        if (!WindowId.TryParse(Read(row, "window_id"), out WindowId window))
        {
            throw new TmuxProtocolException("tmux reported a malformed window identifier.", TmuxDispatchState.Dispatched);
        }
        return new WindowEntityKey(ReadSession(row), window, ReadNumber(row, "window_index"));
    }

    private static SessionId ReadSession(IReadOnlyDictionary<string, string?> row) =>
        SessionId.TryParse(Read(row, "session_id"), out SessionId session)
            ? session
            : throw new TmuxProtocolException("tmux reported a malformed session identifier.", TmuxDispatchState.Dispatched);

    private static int ReadNumber(IReadOnlyDictionary<string, string?> row, string field) =>
        int.TryParse(Read(row, field), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new TmuxProtocolException($"tmux reported an invalid nonnegative integer for '{field}'.", TmuxDispatchState.Dispatched);

    private static string Read(IReadOnlyDictionary<string, string?> row, string field) =>
        row.TryGetValue(field, out string? value) && value is not null
            ? value
            : throw new TmuxProtocolException($"tmux snapshot row is missing '{field}'.", TmuxDispatchState.Dispatched);
}
