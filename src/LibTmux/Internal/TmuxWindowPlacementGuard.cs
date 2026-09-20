using System.Globalization;

namespace LibTmux.Internal;

internal static class TmuxWindowPlacementGuard
{
    internal static IReadOnlyList<string> CreateArguments(WindowEntityKey placement)
    {
        string index = placement.WindowIndex.ToString(CultureInfo.InvariantCulture);
        string expected = $"{placement.SessionId}:{index}:{placement.WindowId}";
        // -F does not yield. An empty success branch keeps the following
        // mutation in the caller's command group, including its failure policy.
        return
        [
            "if-shell", "-F", "-t", $"{placement.SessionId}:{index}",
            $"#{{==:#{{session_id}}:#{{window_index}}:#{{window_id}},{expected}}}",
            string.Empty,
            $"libtmux_window_placement_changed_{Guid.NewGuid():N}",
        ];
    }
}
