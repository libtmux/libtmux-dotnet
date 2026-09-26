using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Resizes a window and controls its pane layout.
public sealed partial class Window
{
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
        if (request.ResolveAdjustment() is int adjustment)
        {
            arguments.Add(adjustment.ToString(CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    /// <summary>Checks layout syntax and builds arguments without reaching tmux.</summary>
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
    /// <remarks>
    /// Feeding a previously captured <see cref="Layout" /> back restores the
    /// same pane sizes on every version, but on tmux 3.7 and earlier it can
    /// rotate which pane lands in which position - measured by hand, not a
    /// hypothetical. tmux 3.8 and newer accepts the JSON form of the layout
    /// (from a plain, non-control client) and restores the exact arrangement,
    /// including which pane id sits where; the classic checksum-prefixed form
    /// never carries pane ids and so cannot.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> SelectLayoutAsync(
        SelectLayoutRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        SelectLayoutRequest options = request ?? new SelectLayoutRequest();
        List<string> arguments = BuildSelectLayoutArguments(options);
        if (options.Layout is string layout)
        {
            await ValidateLayoutAsync(layout, cancellationToken).ConfigureAwait(false);
        }

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

    [UnsupportedOSPlatform("windows")]
    internal async Task ValidateLayoutAsync(string layout, CancellationToken cancellationToken)
    {
        try
        {
            await RequireOwner("layout").ValidateLayoutsAsync([(layout, 1)], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException error)
        {
            throw new TmuxWindowException(
                error.Message, _id, TmuxDispatchState.NotDispatched, error);
        }
    }

    private void ValidateLayout(string layout)
    {
        if (!TmuxLayoutSyntax.IsValidCandidate(layout, 1)
            || (layout.StartsWith('{')
                && !TmuxLayoutSyntax.SupportsJsonLayout(RequireOwner("layout").Version)))
        {
            throw new TmuxWindowException(
                TmuxLayoutSyntax.InvalidLayoutMessage(layout, mirrored: false, RequireOwner("layout").RawVersion!),
                _id,
                TmuxDispatchState.NotDispatched);
        }
    }
}
