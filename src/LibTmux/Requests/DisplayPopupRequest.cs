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
public sealed record DisplayPopupRequest
{
    /// <summary>Initializes a popup request.</summary>
    /// <param name="command">The command the popup runs.</param>
    /// <param name="closeMode">When the popup closes on its own.</param>
    /// <param name="closeExisting">Whether an open popup is closed instead.</param>
    /// <param name="targetClient">The client to show the popup on.</param>
    /// <param name="width">The popup width.</param>
    /// <param name="height">The popup height.</param>
    /// <param name="x">The column to place the popup at.</param>
    /// <param name="y">The row to place the popup at.</param>
    /// <param name="startDirectory">The working directory for the command.</param>
    /// <param name="title">The popup title.</param>
    /// <param name="borderLines">The border line style.</param>
    /// <param name="style">The popup style.</param>
    /// <param name="borderStyle">The popup border style.</param>
    /// <param name="environment">Environment entries set on the command.</param>
    /// <param name="noBorder">Whether the popup has no border.</param>
    /// <param name="closeOnAnyKey">Whether any key closes the popup.</param>
    /// <param name="noKeys">Whether the popup ignores keys.</param>
    public DisplayPopupRequest(
        string? command = null,
        PopupCloseMode? closeMode = null,
        bool closeExisting = false,
        string? targetClient = null,
        string? width = null,
        string? height = null,
        string? x = null,
        string? y = null,
        string? startDirectory = null,
        string? title = null,
        string? borderLines = null,
        string? style = null,
        string? borderStyle = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool noBorder = false,
        bool closeOnAnyKey = false,
        bool noKeys = false)
    {
        if (closeMode is not null && !Enum.IsDefined(closeMode.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(closeMode));
        }

        Command = command;
        CloseMode = closeMode;
        CloseExisting = closeExisting;
        TargetClient = targetClient;
        Width = width;
        Height = height;
        X = x;
        Y = y;
        StartDirectory = startDirectory;
        Title = title;
        BorderLines = borderLines;
        Style = style;
        BorderStyle = borderStyle;
        // The request is read again at dispatch, so a caller that kept the
        // dictionary could otherwise change the argv after constructing it.
        Environment = environment is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(environment, StringComparer.Ordinal));
        NoBorder = noBorder;
        CloseOnAnyKey = closeOnAnyKey;
        NoKeys = noKeys;
    }

    /// <summary>Gets the command the popup runs.</summary>
    public string? Command { get; }

    /// <summary>Gets when the popup closes on its own.</summary>
    public PopupCloseMode? CloseMode { get; }

    /// <summary>Gets whether an open popup is closed instead.</summary>
    public bool CloseExisting { get; }

    /// <summary>Gets the client to show the popup on.</summary>
    public string? TargetClient { get; }

    /// <summary>Gets the popup width.</summary>
    public string? Width { get; }

    /// <summary>Gets the popup height.</summary>
    public string? Height { get; }

    /// <summary>Gets the column to place the popup at.</summary>
    public string? X { get; }

    /// <summary>Gets the row to place the popup at.</summary>
    public string? Y { get; }

    /// <summary>Gets the working directory for the command.</summary>
    /// <remarks>
    /// tmux expands it as a format, so a <c>#</c> in it does not survive
    /// verbatim.
    /// </remarks>
    public string? StartDirectory { get; }

    /// <summary>Gets the popup title.</summary>
    /// <remarks>
    /// tmux expands it as a format, so a <c>#</c> in it does not survive
    /// verbatim.
    /// </remarks>
    public string? Title { get; }

    /// <summary>Gets the border line style.</summary>
    public string? BorderLines { get; }

    /// <summary>Gets the popup style.</summary>
    public string? Style { get; }

    /// <summary>Gets the popup border style.</summary>
    public string? BorderStyle { get; }

    /// <summary>Gets the environment entries set on the command.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; }

    /// <summary>Gets whether the popup has no border.</summary>
    public bool NoBorder { get; }

    /// <summary>Gets whether any key closes the popup.</summary>
    public bool CloseOnAnyKey { get; }

    /// <summary>Gets whether the popup ignores keys.</summary>
    public bool NoKeys { get; }
}
