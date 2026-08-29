using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Links, unlinks, moves, and swaps a window.
public sealed partial class Window
{
    /// <summary>Builds the arguments a link request sends.</summary>
    /// <remarks>
    /// This stays on the window because the source of the link is the session
    /// this handle was read through, which a window resolved by identifier
    /// does not know.
    /// </remarks>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier, so its source link is unknown.
    /// </exception>
    internal List<string> BuildLinkWindowArguments(LinkWindowRequest request)
    {
        List<string> arguments =
        [
            "link-window",
            "-t",
            request.TargetIndex is null
                ? request.TargetSession
                : $"{request.TargetSession}:{request.TargetIndex}",
        ];
        if (request.ReplaceExisting)
        {
            arguments.Add("-k");
        }

        AddDirection(arguments, request.Direction);
        if (request.Detach)
        {
            arguments.Add("-d");
        }

        arguments.Add("-s");
        arguments.Add(SourceLink("link source"));

        return arguments;
    }

    /// <summary>Links this window into another session.</summary>
    /// <param name="request">Where the link goes.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier, so its source link is unknown.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Task LinkAsync(
        LinkWindowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildLinkWindowArguments(request);
        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Removes this window's link to the session it was read through.</summary>
    /// <param name="killIfLast">Whether the window dies when this was its last link.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// tmux refuses to unlink a window that belongs to only one session unless
    /// it is allowed to destroy it.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task UnlinkAsync(
        bool killIfLast = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["unlink-window"];
        if (killIfLast)
        {
            arguments.Add("-k");
        }

        arguments.Add("-t");
        arguments.Add(SourceLink("unlink source"));
        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Builds the arguments a move request sends.</summary>
    /// <remarks>
    /// This stays on the window because both ends come from the handle: the
    /// destination defaults to the session it was read through, and so does
    /// the source it moves from.
    /// </remarks>
    internal List<string> BuildMoveWindowArguments(MoveWindowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string session = request.Session ?? CapturedSession("move destination");
        List<string> arguments = ["move-window", "-t", $"{session}:{request.Destination}"];
        AddDirection(arguments, request.Direction);
        if (request.NoSelect)
        {
            arguments.Add("-d");
        }

        if (request.ReplaceExisting)
        {
            arguments.Add("-k");
        }

        if (request.Renumber)
        {
            arguments.Add("-r");
        }

        arguments.Add("-s");
        arguments.Add(SourceLink("move source"));

        return arguments;
    }

    /// <summary>Moves this window to another index or session.</summary>
    /// <param name="request">Where the window goes.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the state after the move.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> MoveAsync(
        MoveWindowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildMoveWindowArguments(request);

        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(arguments, cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Swaps this window with another.</summary>
    /// <param name="target">The window to swap with.</param>
    /// <param name="detach">Whether the swapped window is left unselected.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// A window linked into several sessions resolves to whichever link tmux
    /// picks, because a window identifier does not name one.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task SwapAsync(
        WindowId target,
        bool detach = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["swap-window", "-t", Target];
        if (detach)
        {
            arguments.Add("-d");
        }

        arguments.Add("-s");
        arguments.Add(target.ToString());
        return RunAsync(arguments, cancellationToken);
    }


    // A bare window id lets tmux choose which link it means, so any operation
    // that moves a link names the session it belongs to as well.
    private string SourceLink(string relation)
    {
        string session = CapturedSession(relation);
        string index = ReadSnapshot("window_index")
            ?? throw new IncompleteSnapshotException(relation, SnapshotDepth.Windows);
        return $"{session}:{index}";
    }

    private string CapturedSession(string relation) =>
        ReadSnapshot("session_id")
        ?? throw new IncompleteSnapshotException(relation, SnapshotDepth.Windows);
}
