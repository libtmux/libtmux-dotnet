using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Describes one <c>new-pane</c> invocation.</summary>
/// <remarks>
/// <c>new-pane</c> arrived in tmux 3.7. Unlike a split it places a floating
/// pane, so it carries a position as well as a size.
/// </remarks>
public sealed record NewPaneRequest : ITmuxRequest<Pane>
{
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly int? _width;
    private readonly int? _height;
    private readonly int? _x;
    private readonly int? _y;

    /// <summary>Gets the window or pane to place against.</summary>
    public string? Target { get; init; }

    /// <summary>Gets the working directory for the new pane.</summary>
    /// <remarks>
    /// tmux expands it as a format before it changes directory, so a <c>#</c>
    /// in it does not survive verbatim.
    /// </remarks>
    public string? StartDirectory { get; init; }

    /// <summary>Gets whether the new pane becomes active.</summary>
    public bool Attach { get; init; }

    /// <summary>Gets the command the new pane runs.</summary>
    public string? Command { get; init; }

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

    /// <summary>Gets the pane width in cells.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Width
    {
        get => _width;
        init
        {
            ThrowIfNotPositive(value, nameof(Width));

            _width = value;
        }
    }

    /// <summary>Gets the pane height in cells.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Height
    {
        get => _height;
        init
        {
            ThrowIfNotPositive(value, nameof(Height));

            _height = value;
        }
    }

    /// <summary>Gets the column to place the pane at.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? X
    {
        get => _x;
        init
        {
            ThrowIfNegative(value, nameof(X));

            _x = value;
        }
    }

    /// <summary>Gets the row to place the pane at.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? Y
    {
        get => _y;
        init
        {
            ThrowIfNegative(value, nameof(Y));

            _y = value;
        }
    }

    /// <summary>Gets whether the new pane is zoomed.</summary>
    public bool Zoom { get; init; }

    /// <summary>Gets whether the pane starts with no command.</summary>
    public bool Empty { get; init; }

    /// <summary>Gets the pane style.</summary>
    public string? Style { get; init; }

    /// <summary>Gets the border style while the pane is active.</summary>
    public string? ActiveBorderStyle { get; init; }

    /// <summary>Gets the border style while the pane is not active.</summary>
    public string? InactiveBorderStyle { get; init; }

    /// <summary>Gets the message shown in the pane.</summary>
    public string? Message { get; init; }

    /// <summary>Gets whether the pane stays after its command exits.</summary>
    public bool KeepOpen { get; init; }

    private static void ThrowIfNotPositive(int? value, string parameterName)
    {
        if (value is int cells && cells <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, cells, "Cells must be positive.");
        }
    }

    private static void ThrowIfNegative(int? value, string parameterName)
    {
        if (value is int position && position < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                position,
                "A position cannot be negative.");
        }
    }

    /// <summary>Returns a floating-pane request as one tmux command.</summary>
    /// <param name="pane">The pane the new one is created from.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// The command arrived whole in tmux 3.7, so batching does not soften the
    /// refusal below that: an older server has nothing to send it to.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="pane" /> is null.</exception>
    /// <exception cref="TmuxVersionTooLowException">tmux is older than 3.7.</exception>
    public TmuxCommand ToCommand(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TmuxChaining.Command([.. pane.BuildNewPaneArguments(this)]) with
        {
            RequiredGeneration = pane.Generation,
        };
    }
}
