using System.Collections.ObjectModel;
using System.Globalization;

namespace LibTmux;

/// <summary>Describes one <c>split-window</c> invocation.</summary>
/// <remarks>
/// Every option is set through an initializer rather than a constructor
/// argument. tmux gains flags, and a constructor that grew one would break
/// every caller already compiled against the old signature; a property added
/// beside these does not.
/// </remarks>
public sealed record SplitPaneRequest : ITmuxRequest<Pane>
{
    private readonly int? _percentage;
    private readonly IReadOnlyDictionary<string, string>? _environment;

    /// <summary>Gets the pane to split, or null for the active one.</summary>
    public string? Target { get; init; }

    /// <summary>Gets the window that must contain the target pane at dispatch.</summary>
    /// <remarks>
    /// When supplied, a nonwaiting native guard checks the resolved target's
    /// window immediately before splitting. Null keeps ordinary tmux target
    /// semantics. This check does not prevent a later move during readback.
    /// </remarks>
    public WindowId? ExpectedWindowId { get; init; }

    /// <summary>Gets the working directory for the new pane.</summary>
    /// <remarks>
    /// tmux expands it as a format before it changes directory, so a <c>#</c>
    /// in it does not survive verbatim.
    /// </remarks>
    public string? StartDirectory { get; init; }

    /// <summary>Gets whether the new pane becomes active.</summary>
    public bool Attach { get; init; }

    /// <summary>Gets where the new pane goes.</summary>
    public PaneDirection? Direction { get; init; }

    /// <summary>Gets whether the split spans the whole window.</summary>
    public bool FullWindow { get; init; }

    /// <summary>Gets whether the new pane is zoomed.</summary>
    public bool Zoom { get; init; }

    /// <summary>Gets the command the new pane runs.</summary>
    public string? Command { get; init; }

    /// <summary>Gets the explicit size in cells.</summary>
    public string? Size { get; init; }

    /// <summary>Gets the size as a percentage of the window.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 100.</exception>
    public int? Percentage
    {
        get => _percentage;
        init
        {
            if (value is int share && share is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Percentage),
                    share,
                    "A percentage runs from 1 to 100.");
            }

            _percentage = value;
        }
    }

    /// <summary>Gets the environment entries set on the new pane.</summary>
    public IReadOnlyDictionary<string, string>? Environment
    {
        get => _environment;

        // The request is read again at dispatch, so a caller that kept the
        // dictionary could otherwise change the argv after building it.
        init => _environment = value is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(value, StringComparer.Ordinal));
    }

    /// <summary>Gets whether the pane starts with no command.</summary>
    public bool Empty { get; init; }

    /// <summary>Gets the pane style.</summary>
    public string? Style { get; init; }

    /// <summary>Gets the border style while the pane is active.</summary>
    public string? ActiveBorderStyle { get; init; }

    /// <summary>Gets the border style while it is not.</summary>
    public string? InactiveBorderStyle { get; init; }

    /// <summary>Gets the message shown in the pane.</summary>
    public string? Message { get; init; }

    /// <summary>Gets whether the pane stays after its command exits.</summary>
    public bool KeepOpen { get; init; }

    /// <summary>Resolves the one size tmux is given, refusing two answers.</summary>
    /// <returns>The <c>-l</c> argument, or null when no size was asked for.</returns>
    /// <exception cref="ArgumentException">Both a size and a percentage are set.</exception>
    /// <remarks>
    /// Initializers cannot check one property against another, so the pairing
    /// is settled where the value is derived. Every caller that sends a split
    /// needs this value, so none can reach tmux having skipped the check.
    /// tmux 3.4 misreads <c>-p</c>, so a percentage rides <c>-l</c> instead,
    /// which every supported version accepts.
    /// </remarks>
    internal string? ResolveSize() =>
        Size is not null && Percentage is not null
            ? throw new ArgumentException(
                "A split is sized in cells or as a percentage, not both.",
                nameof(Percentage))
            : Percentage is int share
                ? string.Create(CultureInfo.InvariantCulture, $"{share}%")
                : Size;

    /// <summary>Returns a split request as one tmux command.</summary>
    /// <param name="pane">The pane being split.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Splitting into an empty pane arrived in tmux 3.7 and the appearance
    /// flags in 3.6, so the pane decides which of them the built command
    /// carries. It prints the new pane's identifier the same way the one-shot
    /// path does.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="pane" /> is null.</exception>
    public TmuxCommand ToCommand(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TmuxChaining.Command([.. pane.BuildSplitArguments(this)]) with
        {
            RequiredGeneration = pane.Generation,
            RequiredTargetWindow = ExpectedWindowId is { } expected ? (Target ?? pane.Id.ToString(), expected) : null,
        };
    }
}
