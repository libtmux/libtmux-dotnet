using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Names when a popup closes on its own.</summary>
public enum PopupCloseMode
{
    /// <summary>Close when the command exits, however it exits.</summary>
    AnyExit = 0,

    /// <summary>Close only when the command exits successfully.</summary>
    SuccessfulExit = 1,
}

/// <summary>Describes one <c>display-popup</c> invocation.</summary>
/// <remarks>
/// A popup needs an attached client and blocks the invoking command until it
/// closes, so a caller with no client, or with a command that never exits, will
/// wait. Cancel the call rather than expecting it to return.
/// </remarks>
public sealed record DisplayPopupRequest : ITmuxRequest<Pane>
{
    private readonly PopupCloseMode? _closeMode;
    private readonly IReadOnlyDictionary<string, string>? _environment;

    /// <summary>Gets the command the popup runs.</summary>
    public string? Command { get; init; }

    /// <summary>Gets when the popup closes on its own.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined mode.</exception>
    public PopupCloseMode? CloseMode
    {
        get => _closeMode;
        init
        {
            if (value is not null && !Enum.IsDefined(value.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(CloseMode));
            }

            _closeMode = value;
        }
    }

    /// <summary>Gets whether an open popup is closed instead.</summary>
    public bool CloseExisting { get; init; }

    /// <summary>Gets the client to show the popup on.</summary>
    public string? TargetClient { get; init; }

    /// <summary>Gets the popup width.</summary>
    public string? Width { get; init; }

    /// <summary>Gets the popup height.</summary>
    public string? Height { get; init; }

    /// <summary>Gets the column to place the popup at.</summary>
    public string? X { get; init; }

    /// <summary>Gets the row to place the popup at.</summary>
    public string? Y { get; init; }

    /// <summary>Gets the working directory for the command.</summary>
    /// <remarks>
    /// tmux expands it as a format, so a <c>#</c> in it does not survive
    /// verbatim.
    /// </remarks>
    public string? StartDirectory { get; init; }

    /// <summary>Gets the popup title.</summary>
    /// <remarks>
    /// tmux expands it as a format, so a <c>#</c> in it does not survive
    /// verbatim.
    /// </remarks>
    public string? Title { get; init; }

    /// <summary>Gets the border line style.</summary>
    public string? BorderLines { get; init; }

    /// <summary>Gets the popup style.</summary>
    public string? Style { get; init; }

    /// <summary>Gets the popup border style.</summary>
    public string? BorderStyle { get; init; }

    /// <summary>Gets the environment entries set on the command.</summary>
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

    /// <summary>Gets whether the popup has no border.</summary>
    public bool NoBorder { get; init; }

    /// <summary>Gets whether any key closes the popup.</summary>
    public bool CloseOnAnyKey { get; init; }

    /// <summary>Gets whether the popup ignores keys.</summary>
    public bool NoKeys { get; init; }

    /// <summary>Returns a popup request as one tmux command.</summary>
    /// <param name="pane">The pane the popup belongs to.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Popup options arrived in tmux 3.3 and the key policy in 3.6, so the
    /// pane decides which of them the built command carries.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="pane" /> is null.</exception>
    public TmuxCommand ToCommand(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return TmuxChaining.Command([.. pane.BuildDisplayPopupArguments(this)]) with
        {
            RequiredGeneration = pane.Generation,
        };
    }
}
