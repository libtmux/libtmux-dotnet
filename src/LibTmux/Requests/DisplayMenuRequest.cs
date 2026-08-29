namespace LibTmux;

/// <summary>Describes one <c>display-menu</c> invocation.</summary>
public sealed record DisplayMenuRequest
{
    private readonly TmuxMenuItem[] _items;

    /// <summary>Initializes a menu.</summary>
    /// <param name="items">The lines the menu offers.</param>
    /// <param name="title">The title shown above them.</param>
    /// <param name="targetPane">The pane the menu belongs to.</param>
    /// <param name="targetClient">The client shown the menu.</param>
    /// <param name="x">Where the menu sits across the screen.</param>
    /// <param name="y">Where the menu sits down the screen.</param>
    /// <param name="startingChoice">The item selected when it opens.</param>
    /// <param name="borderLines">Which line style draws the border.</param>
    /// <param name="style">The style of the menu itself.</param>
    /// <param name="borderStyle">The style of its border.</param>
    /// <param name="selectedStyle">The style of the selected line.</param>
    /// <param name="mouse">Whether the mouse can choose an item.</param>
    /// <param name="stayOpen">Whether the menu stays open after a choice.</param>
    public DisplayMenuRequest(
        IReadOnlyList<TmuxMenuItem> items,
        string? title = null,
        string? targetPane = null,
        string? targetClient = null,
        string? x = null,
        string? y = null,
        string? startingChoice = null,
        string? borderLines = null,
        string? style = null,
        string? borderStyle = null,
        string? selectedStyle = null,
        bool mouse = false,
        bool stayOpen = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("A menu needs at least one item.", nameof(items));
        }

        _items = [.. items];
        Title = title;
        TargetPane = targetPane;
        TargetClient = targetClient;
        X = x;
        Y = y;
        StartingChoice = startingChoice;
        BorderLines = borderLines;
        Style = style;
        BorderStyle = borderStyle;
        SelectedStyle = selectedStyle;
        Mouse = mouse;
        StayOpen = stayOpen;
    }

    /// <summary>Gets the lines the menu offers.</summary>
    public IReadOnlyList<TmuxMenuItem> Items => _items;

    /// <summary>Gets the title shown above them.</summary>
    /// <remarks>
    /// tmux expands it as a format, so a <c>#</c> in it does not survive
    /// verbatim.
    /// </remarks>
    public string? Title { get; }

    /// <summary>Gets the pane the menu belongs to.</summary>
    public string? TargetPane { get; }

    /// <summary>Gets the client shown the menu.</summary>
    public string? TargetClient { get; }

    /// <summary>Gets where the menu sits across the screen.</summary>
    public string? X { get; }

    /// <summary>Gets where the menu sits down the screen.</summary>
    public string? Y { get; }

    /// <summary>Gets the item selected when it opens.</summary>
    public string? StartingChoice { get; }

    /// <summary>Gets which line style draws the border.</summary>
    public string? BorderLines { get; }

    /// <summary>Gets the style of the menu itself.</summary>
    public string? Style { get; }

    /// <summary>Gets the style of its border.</summary>
    public string? BorderStyle { get; }

    /// <summary>Gets the style of the selected line.</summary>
    public string? SelectedStyle { get; }

    /// <summary>Gets whether the mouse can choose an item.</summary>
    public bool Mouse { get; }

    /// <summary>Gets whether the menu stays open after a choice.</summary>
    public bool StayOpen { get; }
}
