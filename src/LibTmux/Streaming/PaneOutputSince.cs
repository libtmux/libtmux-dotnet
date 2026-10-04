using LibTmux.Internal;

namespace LibTmux;

/// <summary>Where a read of a pane's output finished, so the next read returns only what is new.</summary>
/// <remarks>
/// Opaque, and good only for the pane and server process that issued it. It
/// survives scrolling and history trimming: a later read finds its place again
/// or says it could not.
/// </remarks>
public sealed class PaneOutputPosition
{
    internal PaneOutputPosition(PaneCursor cursor) => Cursor = cursor;

    internal PaneCursor Cursor { get; }
}

/// <summary>What a pane printed since a position, and where this read finished.</summary>
/// <param name="Lines">The new lines, oldest first; none on a read that starts without a position.</param>
/// <param name="Position">Where this read finished; pass it to the next read.</param>
/// <param name="LinesMissed">
/// Whether output may be missing: scrollback dropped lines before this read
/// saw them, or the position could not be found again. <paramref name="Lines" />
/// is then what the pane shows rather than exactly what is new.
/// </param>
public sealed record PaneOutputSince(IReadOnlyList<string> Lines, PaneOutputPosition Position, bool LinesMissed);
