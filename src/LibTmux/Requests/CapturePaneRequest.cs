namespace LibTmux;

/// <summary>Names one end of a capture range.</summary>
/// <remarks>
/// tmux writes the extremes as a literal <c>-</c> rather than a number, which
/// means the beginning of the history for a start and the end of the visible
/// pane for an end. The two named values carry that same absent line number and
/// differ only in which flag they read well against.
/// </remarks>
public readonly record struct CapturePanePosition
{
    /// <summary>Initializes a position at one line.</summary>
    /// <param name="lineNumber">The line, where zero is the top of the visible pane.</param>
    public CapturePanePosition(int lineNumber) => LineNumber = lineNumber;

    /// <summary>Gets the oldest line tmux still holds.</summary>
    public static CapturePanePosition BeginningOfHistory => default;

    /// <summary>Gets the last line of the visible pane.</summary>
    public static CapturePanePosition EndOfVisiblePane => default;

    /// <summary>Gets the line, or null for the extreme tmux writes as <c>-</c>.</summary>
    public int? LineNumber { get; }
}

/// <summary>Describes one <c>capture-pane</c> invocation.</summary>
public sealed record CapturePaneRequest : ITmuxRequest<Pane>
{
    /// <summary>Gets the first line to capture.</summary>
    public CapturePanePosition? StartLine { get; init; }

    /// <summary>Gets the last line to capture.</summary>
    public CapturePanePosition? EndLine { get; init; }

    /// <summary>Gets whether escape sequences are preserved.</summary>
    public bool EscapeSequences { get; init; }

    /// <summary>Gets whether unprintable bytes are escaped as octal.</summary>
    public bool EscapeNonPrintable { get; init; }

    /// <summary>Gets whether wrapped lines are joined.</summary>
    /// <remarks>tmux applies this in place of trailing-space handling.</remarks>
    public bool JoinWrappedLines { get; init; }

    /// <summary>Gets whether trailing spaces are kept.</summary>
    public bool PreserveTrailingSpaces { get; init; }

    /// <summary>Gets whether trailing spaces are removed.</summary>
    public bool TrimTrailingSpaces { get; init; }

    /// <summary>Gets whether the alternate screen is captured.</summary>
    /// <remarks>
    /// A pane with no alternate screen makes this an error unless
    /// <see cref="Quiet" /> is set.
    /// </remarks>
    public bool AlternateScreen { get; init; }

    /// <summary>Gets whether a missing alternate screen is not an error.</summary>
    /// <remarks>This does not quieten a target that cannot be resolved.</remarks>
    public bool Quiet { get; init; }

    /// <summary>Gets whether the pane's mode screen is captured.</summary>
    public bool ModeScreen { get; init; }

    /// <summary>Gets whether pending output is captured.</summary>
    public bool Pending { get; init; }

    /// <summary>Gets whether hyperlinks are captured.</summary>
    public bool Hyperlinks { get; init; }

    /// <summary>Gets whether each line carries its number.</summary>
    public bool LineNumbers { get; init; }

    /// <summary>Gets whether each line carries its flags.</summary>
    public bool LineFlags { get; init; }

    /// <summary>Returns a capture request as one tmux command.</summary>
    /// <param name="pane">The pane being captured.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Several capture flags arrived after tmux 3.2a, and the pane is what
    /// knows which tmux is answering, so the command it builds carries only
    /// the flags that server accepts.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="pane" /> is null.</exception>
    public TmuxCommand ToCommand(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TmuxChaining.Command([.. pane.BuildCaptureArguments(["-p"], this)]) with
        {
            RequiredGeneration = pane.Generation,
        };
    }
}
