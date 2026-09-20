using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Links, unlinks, moves, and swaps a window.
public sealed partial class Window
{
    /// <summary>Builds the arguments a link request sends.</summary>
    /// <remarks>
    /// The source names the session and index captured with this window.
    /// </remarks>
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
    /// <remarks>
    /// A guard in the native queue rejects the command when the captured
    /// source index belongs to a different window or no longer exists.
    /// </remarks>
    /// <param name="request">Where the link goes.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task LinkAsync(
        LinkWindowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildLinkWindowArguments(request);
        return RunPlacementAsync(arguments, cancellationToken);
    }

    /// <summary>Removes this window's link to the session it was read through.</summary>
    /// <param name="killIfLast">Whether the window dies when this was its last link.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// tmux refuses to unlink a window that belongs to only one session unless
    /// it is allowed to destroy it. A guard in the native queue checks the
    /// captured session/index still names this window before unlinking it.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task UnlinkAsync(
        bool killIfLast = false,
        CancellationToken cancellationToken = default) =>
        RunPlacementAsync(BuildUnlinkArguments(killIfLast), cancellationToken);

    /// <summary>Unlinks this placement only while its pane membership is unchanged.</summary>
    /// <param name="killIfLast">Whether the window dies when this was its last link.</param>
    /// <param name="expectedPaneIds">The nonempty, distinct pane IDs expected in the window, in any order.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// The IDs are copied before dispatch. The same nonwaiting native queue
    /// checks server generation, captured placement, and exact pane membership
    /// before unlinking. A moved-in or missing pane refuses cleanup; reordering
    /// the same panes is allowed. Observe membership through <see cref="GetPanesAsync" />; this guard does not
    /// establish ownership of the IDs supplied by the caller.
    /// </remarks>
    /// <exception cref="ArgumentException">The IDs are empty, duplicated, or exceed the native command byte budget.</exception>
    /// <exception cref="TmuxCommandException">The placement or pane membership changed, or tmux refused the unlink.</exception>
    [UnsupportedOSPlatform("windows")]
    public Task UnlinkAsync(
        bool killIfLast,
        IReadOnlyList<PaneId> expectedPaneIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedPaneIds);
        PaneId[] copy = [.. expectedPaneIds];
        if (copy.Length == 0 || copy.Distinct().Count() != copy.Length)
        {
            throw new ArgumentException("Expected pane IDs must be nonempty and distinct.", nameof(expectedPaneIds));
        }

        Array.Sort(copy);
        TmuxCommand command = BuildPlacementCommand(BuildUnlinkArguments(killIfLast)) with
        {
            RequiredWindowPaneMembership = TmuxWindowPlacementGuard.CreatePaneMembership(copy),
        };
        TmuxCommandRequest guarded = TmuxGenerationGuard.CreateRequest(
            _generation, [.. command.ToDispatchCommands()], new string('x', TmuxGenerationGuard.MarkerLength));
        if (!guarded.FitsNativeArgumentBudget())
        {
            throw new ArgumentException("The pane guard exceeds the native tmux command byte budget.", nameof(expectedPaneIds));
        }

        return RunPlacementAsync(command, cancellationToken);
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
    internal TmuxCommand BuildPlacementCommand(IReadOnlyList<string> arguments) =>
        new(arguments[0], [.. arguments.Skip(1)])
        {
            RequiredGeneration = _generation,
            RequiredWindowPlacement = new WindowEntityKey(SessionId.Parse(CapturedSession("source placement")), _id, Index),
        };

    [UnsupportedOSPlatform("windows")]
    private Task<TmuxCommandResult> RunPlacementAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunPlacementAsync(BuildPlacementCommand(arguments), cancellationToken);

    [UnsupportedOSPlatform("windows")]
    private Task<TmuxCommandResult> RunPlacementAsync(TmuxCommand command, CancellationToken cancellationToken) =>
        RequireOwner("window placement mutation").Chain().Then(command)
            .ExecuteAsync(cancellationToken);

    private List<string> BuildUnlinkArguments(bool killIfLast)
    {
        List<string> arguments = ["unlink-window"];
        if (killIfLast)
        {
            arguments.Add("-k");
        }
        arguments.Add("-t");
        arguments.Add(SourceLink("unlink source"));
        return arguments;
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
