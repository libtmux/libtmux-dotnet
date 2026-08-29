using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Resizes a window and controls its pane layout.
public sealed partial class Window
{
    // tmux 3.3a crashes its entire server when layout_parse rejects a name, so
    // a layout is checked here rather than by the server. These five are known
    // to every supported version; the mirrored pair arrived in 3.5.
    private static readonly string[] UniversalLayouts =
    [
        "even-horizontal",
        "even-vertical",
        "main-horizontal",
        "main-vertical",
        "tiled",
    ];
    private static readonly string[] MirroredLayouts =
    [
        "main-horizontal-mirrored",
        "main-vertical-mirrored",
    ];

    /// <summary>Resizes this window.</summary>
    /// <param name="request">The size to apply.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the new size.</returns>
    /// <remarks>
    /// Resizing switches the window's <c>window-size</c> option to manual, so
    /// it stops following its clients.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> ResizeAsync(
        ResizeWindowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildResizeWindowArguments(request);

        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(arguments, cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    internal List<string> BuildResizeWindowArguments(ResizeWindowRequest request)
    {
        List<string> arguments = ["resize-window", "-t", Target];
        if (request.Direction is ResizeDirection direction)
        {
            arguments.Add(CommandFlagCatalog.GetResizeDirectionFlag(direction));
        }

        AddValue(arguments, "-x", request.Width);
        AddValue(arguments, "-y", request.Height);
        if (request.Mode is WindowResizeMode mode)
        {
            arguments.Add(mode == WindowResizeMode.Expand ? "-A" : "-a");
        }

        // tmux takes the adjustment as the trailing positional; as a flag value
        // it would be read as a second argument and refused.
        if (request.Adjustment is int adjustment)
        {
            arguments.Add(adjustment.ToString(CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    /// <summary>Builds the arguments a layout request sends.</summary>
    /// <remarks>
    /// This stays on the window rather than becoming a static helper because
    /// validating a layout name asks the running tmux which names it knows,
    /// and an unrecognised name takes the whole server down on 3.3a. A chained
    /// layout has to be checked the same way a direct one is.
    /// </remarks>
    internal List<string> BuildSelectLayoutArguments(SelectLayoutRequest request)
    {
        List<string> arguments = ["select-layout", "-t", Target];
        if (request.Mode is SelectLayoutMode mode)
        {
            arguments.Add(mode switch
            {
                SelectLayoutMode.Spread => "-E",
                SelectLayoutMode.Next => "-n",
                _ => "-p",
            });
        }

        if (request.Layout is not null)
        {
            ValidateLayout(request.Layout);
            arguments.Add(request.Layout);
        }

        return arguments;
    }

    /// <summary>Applies a layout to this window.</summary>
    /// <param name="request">The layout to apply.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the new layout.</returns>
    /// <exception cref="TmuxWindowException">
    /// The layout is one tmux may not recognise.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> SelectLayoutAsync(
        SelectLayoutRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        SelectLayoutRequest options = request ?? new SelectLayoutRequest();
        List<string> arguments = BuildSelectLayoutArguments(options);

        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(arguments, cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Moves to the next layout.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the new layout.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> SelectNextLayoutAsync(
        CancellationToken cancellationToken = default)
    {
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["next-layout", "-t", Target], cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Moves to the previous layout.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the new layout.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> SelectPreviousLayoutAsync(
        CancellationToken cancellationToken = default)
    {
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["previous-layout", "-t", Target], cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    private void ValidateLayout(string layout)
    {
        if (layout.Length == 0)
        {
            throw new TmuxWindowException("A layout name cannot be empty.", _id);
        }

        // A layout tmux dumped begins with a four-digit hexadecimal checksum,
        // and every version parses those. Named layouts are checked against the
        // set the running tmux knows.
        if (HasCustomLayoutPrefix(layout)
            || UniversalLayouts.Contains(layout, StringComparer.Ordinal))
        {
            return;
        }

        Server owner = RequireOwner("layout");
        bool mirroredKnown = owner.Version is TmuxVersion version
            && version >= TmuxVersion.Parse("3.5");
        if (mirroredKnown && MirroredLayouts.Contains(layout, StringComparer.Ordinal))
        {
            return;
        }

        throw new TmuxWindowException(
            $"tmux {owner.RawVersion} does not know the layout '{layout}'.",
            _id);
    }

    private static bool HasCustomLayoutPrefix(string layout) =>
        layout.Length > 5
        && layout[4] == ','
        && char.IsAsciiHexDigit(layout[0])
        && char.IsAsciiHexDigit(layout[1])
        && char.IsAsciiHexDigit(layout[2])
        && char.IsAsciiHexDigit(layout[3]);
}
