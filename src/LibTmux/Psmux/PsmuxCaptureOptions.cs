namespace LibTmux;

/// <summary>Chooses the psmux pane text that can be captured safely.</summary>
public sealed class PsmuxCaptureOptions
{
    /// <summary>Initializes a bounded psmux capture.</summary>
    /// <param name="startLine">The first line, or <see langword="null" /> for psmux's default.</param>
    /// <param name="endLine">The last line, or <see langword="null" /> for psmux's default.</param>
    /// <param name="escapeSequences">Whether terminal escape sequences are preserved.</param>
    /// <param name="joinWrappedLines">Whether wrapped screen rows are joined.</param>
    public PsmuxCaptureOptions(
        CapturePanePosition? startLine = null,
        CapturePanePosition? endLine = null,
        bool escapeSequences = false,
        bool joinWrappedLines = false)
    {
        StartLine = startLine;
        EndLine = endLine;
        EscapeSequences = escapeSequences;
        JoinWrappedLines = joinWrappedLines;
    }

    /// <summary>Gets the first line to capture.</summary>
    public CapturePanePosition? StartLine { get; }

    /// <summary>Gets the last line to capture.</summary>
    public CapturePanePosition? EndLine { get; }

    /// <summary>Gets whether terminal escape sequences are preserved.</summary>
    public bool EscapeSequences { get; }

    /// <summary>Gets whether wrapped screen rows are joined.</summary>
    public bool JoinWrappedLines { get; }

    internal CapturePaneRequest ToRequest() => new()
    {
        StartLine = StartLine,
        EndLine = EndLine,
        EscapeSequences = EscapeSequences,
        JoinWrappedLines = JoinWrappedLines,
    };
}
