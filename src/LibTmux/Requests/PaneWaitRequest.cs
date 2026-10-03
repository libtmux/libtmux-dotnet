using System.Text.RegularExpressions;

namespace LibTmux;

/// <summary>Describes output to wait for in a pane.</summary>
/// <remarks>
/// Patterns are tested line by line, against what the screen shows when the
/// wait begins and then against every row the pane writes or rewrites. Text
/// typed into the pane is output too, so wait for something the command line
/// itself does not contain.
/// </remarks>
public sealed record PaneWaitRequest
{
    /// <summary>Gets the patterns that end the wait; none means any new output does.</summary>
    public IReadOnlyList<Regex> Patterns { get; init; } = [];

    /// <summary>Gets the patterns meaning the awaited output will never come.</summary>
    /// <remarks>Only new output is tested against these.</remarks>
    public IReadOnlyList<Regex> StopPatterns { get; init; } = [];

    /// <summary>Gets how long to wait.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
