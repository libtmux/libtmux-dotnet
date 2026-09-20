using System.Globalization;

namespace LibTmux.Internal;

internal static class TmuxWindowPlacementGuard
{
    internal static string CreatePaneMembership(IReadOnlyList<PaneId> sortedIds)
    {
        // P: iteration order differs between supported tmux versions.
        // Count plus membership makes equality independent of that order.
        string accepted = AnyPaneId(sortedIds, 0, sortedIds.Count);
        string count = sortedIds.Count.ToString(CultureInfo.InvariantCulture);
        return "#{&&:#{==:#{window_panes}," + count + "},#{==:#{P:#{?" + accepted + ",,x}},}}";
    }

    internal static IReadOnlyList<string> CreateTargetWindowArguments(string target, WindowId windowId) =>
        [
            "if-shell", "-F", "-t", target,
            $"#{{==:#{{window_id}},{windowId}}}",
            string.Empty,
            $"libtmux_pane_window_changed_{Guid.NewGuid():N}",
        ];

    private static string AnyPaneId(IReadOnlyList<PaneId> ids, int offset, int count)
    {
        if (count == 1)
        {
            return $"#{{==:#{{pane_id}},{ids[offset]}}}";
        }
        int left = count / 2;
        return $"#{{||:{AnyPaneId(ids, offset, left)},{AnyPaneId(ids, offset + left, count - left)}}}";
    }

    internal static IReadOnlyList<string> CreateArguments(WindowEntityKey placement, string? paneMembership = null)
    {
        string index = placement.WindowIndex.ToString(CultureInfo.InvariantCulture);
        string expected = $"{placement.SessionId}:{index}:{placement.WindowId}";
        string condition = $"#{{==:#{{session_id}}:#{{window_index}}:#{{window_id}},{expected}}}";
        if (paneMembership is not null)
        {
            condition = $"#{{&&:{condition},{paneMembership}}}";
        }
        // -F does not yield. An empty success branch keeps the following
        // mutation in the caller's command group, including its failure policy.
        return
        [
            "if-shell", "-F", "-t", $"{placement.SessionId}:{index}",
            condition,
            string.Empty,
            $"libtmux_window_placement_changed_{Guid.NewGuid():N}",
        ];
    }
}
