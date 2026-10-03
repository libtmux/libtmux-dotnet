using System.Globalization;
using System.Runtime.Versioning;

namespace LibTmux.Internal;

/// <summary>Where a pane's grid stands at one instant.</summary>
/// <param name="PanePid">The process the pane started. A new one means a new pane.</param>
/// <param name="HistorySize">Lines currently held in scrollback.</param>
/// <param name="HistoryLimit">The most lines scrollback will hold.</param>
/// <param name="PaneHeight">Visible rows.</param>
/// <param name="CursorY">The cursor's row within the visible screen.</param>
/// <param name="Dead">Whether the pane's program has exited.</param>
/// <param name="AlternateScreen">Whether a full-screen program owns the pane.</param>
/// <remarks>
/// Read in one tmux call rather than field by field. Two reads of a moving
/// pane describe two different instants, and a position computed across them
/// belongs to neither.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed record PaneGridState(
    string PanePid,
    int HistorySize,
    int HistoryLimit,
    int PaneHeight,
    int CursorY,
    bool Dead,
    bool AlternateScreen)
{
    private const string Format =
        "#{pane_pid}\t#{history_size}\t#{history_limit}\t#{pane_height}"
        + "\t#{cursor_y}\t#{pane_dead}\t#{alternate_on}";

    /// <summary>Gets the absolute position of the row the cursor is on.</summary>
    /// <remarks>
    /// Counted from the oldest line tmux still holds. It survives scrolling,
    /// which a row number within the visible screen does not.
    /// </remarks>
    internal int CursorAbsolute => HistorySize + CursorY;

    /// <summary>Reads a pane's grid state in one tmux call.</summary>
    /// <param name="pane">The pane to read.</param>
    /// <param name="cancellationToken">Cancels the tmux query.</param>
    /// <returns>The state, or null when tmux answered nothing readable.</returns>
    internal static async Task<PaneGridState?> ReadAsync(
        Pane pane,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? lines = await pane.DisplayMessageAsync(
                new DisplayMessageRequest { Message = Format, ReturnText = true },
                cancellationToken)
            .ConfigureAwait(false);
        if (lines is not { Count: > 0 } || lines[0] is not { } line)
        {
            return null;
        }

        string[] parts = line.Split('\t');
        if (parts.Length < 7)
        {
            return null;
        }

        return new PaneGridState(
            PanePid: parts[0],
            HistorySize: Int(parts[1]),
            HistoryLimit: Int(parts[2]),
            PaneHeight: Int(parts[3]),
            CursorY: Int(parts[4]),
            Dead: parts[5] == "1",
            AlternateScreen: parts[6] == "1");
    }

    private static int Int(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
}
