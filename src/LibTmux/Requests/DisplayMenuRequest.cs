namespace LibTmux;

/// <summary>Describes one <c>display-menu</c> invocation.</summary>
public sealed record DisplayMenuRequest : ITmuxRequest<Server>
{
    private readonly TmuxMenuItem[] _items;

    /// <summary>Initializes a menu.</summary>
    /// <param name="items">The lines the menu offers.</param>
    public DisplayMenuRequest(IReadOnlyList<TmuxMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("A menu needs at least one item.", nameof(items));
        }

        _items = [.. items];
    }

    /// <summary>Gets the lines the menu offers.</summary>
    public IReadOnlyList<TmuxMenuItem> Items => _items;

    /// <summary>Gets the title shown above them.</summary>
    /// <remarks>
    /// tmux expands it as a format, so a <c>#</c> in it does not survive
    /// verbatim.
    /// </remarks>
    public string? Title { get; init; }

    /// <summary>Gets the pane the menu belongs to.</summary>
    public string? TargetPane { get; init; }

    /// <summary>Gets the client shown the menu.</summary>
    public string? TargetClient { get; init; }

    /// <summary>Gets where the menu sits across the screen.</summary>
    public string? X { get; init; }

    /// <summary>Gets where the menu sits down the screen.</summary>
    public string? Y { get; init; }

    /// <summary>Gets the item selected when it opens.</summary>
    public string? StartingChoice { get; init; }

    /// <summary>Gets which line style draws the border.</summary>
    public string? BorderLines { get; init; }

    /// <summary>Gets the style of the menu itself.</summary>
    public string? Style { get; init; }

    /// <summary>Gets the style of its border.</summary>
    public string? BorderStyle { get; init; }

    /// <summary>Gets the style of the selected line.</summary>
    public string? SelectedStyle { get; init; }

    /// <summary>Gets whether the mouse can choose an item.</summary>
    public bool Mouse { get; init; }

    /// <summary>Gets whether the menu stays open after a choice.</summary>
    public bool StayOpen { get; init; }

    /// <summary>Returns a menu request as one tmux command.</summary>
    /// <param name="server">The server the menu is shown on.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// The style flags arrived in tmux 3.4 and the mouse flag in 3.5, so the
    /// server decides which of them the built command carries.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="server" /> is null.</exception>
    public TmuxCommand ToCommand(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return TmuxChaining.Command([.. server.BuildDisplayMenuArguments(this)]);
    }
}
