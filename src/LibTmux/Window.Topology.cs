using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux;

/// <summary>Names which way a window's panes rotate.</summary>
public enum WindowRotationDirection
{
    /// <summary>Rotate panes towards the top of the window.</summary>
    Up = 0,

    /// <summary>Rotate panes towards the bottom of the window.</summary>
    Down = 1,
}

// Window mutations return replacements when a truthful handle remains;
// destructive or re-homing operations do not.
public sealed partial class Window
{
    private const string DisplayMessageLiteralCapability = "display_message_literal";
    private const string NewPaneCommandCapability = "new_pane_command";
    private const string SplitWindowEmptyCapability = "split_window_empty";
    private const string SplitWindowAppearanceCapability = "split_window_appearance";

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

    /// <summary>Gets the window name captured with this handle.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    public string Name =>
        ReadSnapshot("window_name")
        ?? throw new IncompleteSnapshotException("name", SnapshotDepth.Windows);

    /// <summary>Gets the index this window holds in its session.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    /// <remarks>
    /// A window linked into several sessions holds a different index in each,
    /// so this is the index of the session this handle was read through.
    /// </remarks>
    public int Index => ReadCapturedInt("window_index", "index");

    /// <summary>Gets the window height captured with this handle.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    public int Height => ReadCapturedInt("window_height", "height");

    /// <summary>Gets the window width captured with this handle.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    public int Width => ReadCapturedInt("window_width", "width");

    /// <summary>Gets the server that owns this window.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    public Server Server => RequireOwner("server");

    /// <summary>Gets the session this window was read through.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The window was resolved by identifier rather than materialized.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Session Session =>
        SessionId.TryParse(ReadSnapshot("session_id"), out SessionId id)
            ? new Session(RequireConnection(), _generation, id)
            : throw new IncompleteSnapshotException("session", SnapshotDepth.Windows);

    /// <summary>Re-reads this window from tmux.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying current state.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Listing by -t would return the whole session and would fail loudly on
        // a window that is already gone, so the whole server is listed and the
        // row is selected here. A linked window yields one row per session.
        Server owner = RequireOwner("refresh");
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await RelationReader
            .ListAsync(owner, "list-windows", ["-a"], cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<Window> windows = [.. rows
            .Select(row => RelationReader.ToWindow(owner, row))
            .Where(window => window.Id == _id)];
        string? session = ReadSnapshot("session_id");
        return windows.FirstOrDefault(window => window.ReadSnapshot("session_id") == session)
            ?? (windows.Count > 0 ? windows[0] : null)
            ?? throw new TmuxObjectNotFoundException(
                $"tmux no longer has window '{_id}'.",
                _id.ToString());
    }

    /// <summary>Renames this window.</summary>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the new name.</returns>
    /// <remarks>
    /// tmux expands the name as a format, so a <c>#</c> in it does not survive
    /// verbatim.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> RenameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["rename-window", "-t", Target, name], cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Selects this window in its session.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the state after selection.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> SelectAsync(CancellationToken cancellationToken = default)
    {
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["select-window", "-t", Target], cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Stops this window.</summary>
    /// <param name="allExcept">Whether every other window in the session is stopped instead.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task KillAsync(bool allExcept = false, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["kill-window"];
        if (allExcept)
        {
            arguments.Add("-a");
        }

        arguments.Add("-t");
        arguments.Add(Target);
        return RunAsync(arguments, cancellationToken);
    }

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

    /// <summary>Rotates the panes in this window.</summary>
    /// <param name="direction">Which way to rotate, or null for tmux's default.</param>
    /// <param name="keepZoom">Whether a zoomed pane stays zoomed.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the state after rotation.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> RotateAsync(
        WindowRotationDirection? direction = null,
        bool keepZoom = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["rotate-window", "-t", Target];
        if (direction is WindowRotationDirection rotation)
        {
            arguments.Add(rotation == WindowRotationDirection.Up ? "-U" : "-D");
        }

        if (keepZoom)
        {
            arguments.Add("-Z");
        }

        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(arguments, cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Restarts the command running in this window.</summary>
    /// <param name="request">What to respawn, or null to reuse the original.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// tmux refuses to respawn a window that is still running unless the
    /// request kills it first, and killing it destroys every pane but one.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task RespawnAsync(
        RespawnRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        RespawnRequest options = request ?? new RespawnRequest();
        List<string> arguments = ["respawn-window", "-t", Target];
        if (options.KillExistingProcess)
        {
            arguments.Add("-k");
        }

        AddValue(arguments, "-c", StartDirectory.Resolve(options.StartDirectory));
        AddEnvironment(arguments, options.Environment);
        if (options.Command is not null)
        {
            arguments.Add(options.Command);
        }

        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Creates a window next to this one.</summary>
    /// <param name="request">The window to create.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The created window.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> CreateWindowAsync(
        NewWindowRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        NewWindowRequest options = (request ?? new NewWindowRequest()).WithTargetWindow(Target);
        Server owner = RequireOwner("windows");
        List<string> arguments = ["new-window", "-P", "-F", "#{window_id}", "-t", Target];
        if (!options.Attach)
        {
            arguments.Add("-d");
        }

        if (options.KillExisting)
        {
            arguments.Add("-k");
        }

        if (options.SelectExisting)
        {
            arguments.Add("-S");
        }

        AddDirection(arguments, options.Direction);
        AddValue(arguments, "-n", options.Name);
        AddValue(arguments, "-c", StartDirectory.Resolve(options.StartDirectory));
        AddEnvironment(arguments, options.Environment);
        if (options.Command is not null)
        {
            arguments.Add(options.Command);
        }

        var sequence = new TmuxMutationSequence();
        TmuxCommandResult result = await sequence.MutateAsync(
                () => _commandDispatcher.ExecuteAsync(arguments, cancellationToken),
                static value => TmuxCommandFailure.ThrowIfFailed(value, "new-window"))
            .ConfigureAwait(false);

        WindowId created = sequence.Observe(() =>
            result.StandardOutputLines.Count > 0
                && WindowId.TryParse(result.StandardOutputLines[0], out WindowId parsed)
                    ? parsed
                    : throw new InvalidDataException("tmux reported no new window identifier."));

        IReadOnlyList<Window> windows = await sequence
            .ObserveAsync(() => owner.GetWindowsAsync(cancellationToken))
            .ConfigureAwait(false);
        return sequence.Observe(() =>
            windows.FirstOrDefault(window => window.Id == created)
                ?? throw new TmuxObjectNotFoundException(
                    $"tmux did not report the created window '{created}'.",
                    created.ToString()));
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

    /// <summary>Selects a pane in this window.</summary>
    /// <param name="target">A pane target, or a direction such as <c>-U</c>.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The pane that is active afterwards, or null when none is.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane?> SelectPaneAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        // A bare pane index would resolve against the caller's current session,
        // so anything that is not a direction flag is anchored to this window.
        List<string> arguments = target is "-l" or "-U" or "-D" or "-L" or "-R"
            ? ["select-pane", "-t", Target, target]
            : ["select-pane", "-t", $"{Target}.{target}"];
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(arguments, cancellationToken),
                async () =>
                {
                    IReadOnlyList<Pane> panes = await GetPanesAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return panes.FirstOrDefault(pane => pane.Snapshot?["pane_active"] == "1");
                })
            .ConfigureAwait(false);
    }

    /// <summary>Reads one pane in this window.</summary>
    /// <param name="target">The pane target.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The pane, or null when this window has no such pane.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane?> GetPaneAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        IReadOnlyList<Pane> panes = await GetPanesAsync(cancellationToken).ConfigureAwait(false);
        return panes.FirstOrDefault(pane =>
            pane.Id.ToString() == target
            || pane.Snapshot?["pane_index"] == target);
    }

    /// <summary>Splits a pane in this window.</summary>
    /// <param name="request">How to split.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The created pane.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> SplitPaneAsync(
        SplitPaneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        SplitPaneRequest options = request ?? new SplitPaneRequest();
        Server owner = RequireOwner("panes");
        List<string> arguments =
        [
            "split-window",
            "-P",
            "-F",
            "#{pane_id}",
            "-t",
            options.Target is null ? Target : $"{Target}.{options.Target}",
        ];
        foreach (string flag in CommandFlagCatalog.GetPaneDirectionFlags(
            options.Direction ?? PaneDirection.Below))
        {
            arguments.Add(flag);
        }

        // tmux 3.4 misreads -p, so a percentage rides the -l flag instead,
        // which every supported version accepts.
        AddValue(
            arguments,
            "-l",
            options.Percentage is int share
                ? string.Create(CultureInfo.InvariantCulture, $"{share}%")
                : options.Size);
        if (options.FullWindow)
        {
            arguments.Add("-f");
        }

        if (options.Zoom)
        {
            arguments.Add("-Z");
        }

        if (!options.Attach)
        {
            arguments.Add("-d");
        }

        AddValue(arguments, "-c", StartDirectory.Resolve(options.StartDirectory));
        AddEnvironment(arguments, options.Environment);
        AddSplitAppearance(arguments, options);
        if (options.Command is not null)
        {
            arguments.Add(options.Command);
        }

        var sequence = new TmuxMutationSequence();
        TmuxCommandResult result = await sequence.MutateAsync(
                () => _commandDispatcher.ExecuteAsync(arguments, cancellationToken),
                static value => TmuxCommandFailure.ThrowIfFailed(value, "split-window"))
            .ConfigureAwait(false);
        PaneId created = sequence.Observe(() =>
            result.StandardOutputLines.Count > 0
                && PaneId.TryParse(result.StandardOutputLines[0], out PaneId parsed)
                    ? parsed
                    : throw new InvalidDataException("tmux reported no new pane identifier."));

        IReadOnlyList<Pane> panes = await sequence
            .ObserveAsync(() => GetPanesAsync(cancellationToken))
            .ConfigureAwait(false);
        return sequence.Observe(() =>
            panes.FirstOrDefault(pane => pane.Id == created)
                ?? throw new TmuxObjectNotFoundException(
                    $"tmux did not report the created pane '{created}'.",
                    created.ToString()));
    }

    /// <summary>Creates a floating pane in this window.</summary>
    /// <param name="request">The pane to create.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The created pane.</returns>
    /// <exception cref="TmuxVersionTooLowException">
    /// The server predates tmux 3.7, which introduced the command.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> CreatePaneAsync(
        NewPaneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        NewPaneRequest options = request ?? new NewPaneRequest();
        Server owner = RequireOwner("panes");
        // The whole command is missing before 3.7, so there is nothing to omit
        // and nothing worth dispatching.
        if (!Supports(owner, NewPaneCommandCapability))
        {
            throw new TmuxVersionTooLowException(
                "new-pane requires tmux 3.7.",
                TmuxVersion.Parse("3.7"),
                owner.Version ?? default);
        }

        List<string> arguments =
        [
            "new-pane",
            "-P",
            "-F",
            "#{pane_id}",
            "-t",
            options.Target is null ? Target : $"{Target}.{options.Target}",
        ];
        if (!options.Attach)
        {
            arguments.Add("-d");
        }

        AddValue(arguments, "-x", options.Width);
        AddValue(arguments, "-y", options.Height);
        AddValue(arguments, "-X", options.X);
        AddValue(arguments, "-Y", options.Y);
        if (options.Zoom)
        {
            arguments.Add("-Z");
        }

        AddValue(arguments, "-c", StartDirectory.Resolve(options.StartDirectory));
        AddEnvironment(arguments, options.Environment);
        if (options.Empty)
        {
            arguments.Add("-E");
        }

        AddValue(arguments, "-s", options.Style);
        AddValue(arguments, "-S", options.ActiveBorderStyle);
        AddValue(arguments, "-R", options.InactiveBorderStyle);
        AddValue(arguments, "-m", options.Message);
        if (options.KeepOpen)
        {
            arguments.Add("-k");
        }

        if (options.Command is not null)
        {
            arguments.Add(options.Command);
        }

        var sequence = new TmuxMutationSequence();
        TmuxCommandResult result = await sequence.MutateAsync(
                () => _commandDispatcher.ExecuteAsync(arguments, cancellationToken),
                static value => TmuxCommandFailure.ThrowIfFailed(value, "new-pane"))
            .ConfigureAwait(false);
        PaneId created = sequence.Observe(() =>
            result.StandardOutputLines.Count > 0
                && PaneId.TryParse(result.StandardOutputLines[0], out PaneId parsed)
                    ? parsed
                    : throw new InvalidDataException("tmux reported no new pane identifier."));

        IReadOnlyList<Pane> panes = await sequence
            .ObserveAsync(() => GetPanesAsync(cancellationToken))
            .ConfigureAwait(false);
        return sequence.Observe(() =>
            panes.FirstOrDefault(pane => pane.Id == created)
                ?? throw new TmuxObjectNotFoundException(
                    $"tmux did not report the created pane '{created}'.",
                    created.ToString()));
    }

    /// <summary>Runs a tmux-side filter over this window's panes.</summary>
    /// <param name="filter">The raw tmux filter expression.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The panes tmux kept.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Pane>> SearchPanesAsync(
        UnsafeTmuxFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Server owner = RequireOwner("panes");
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await RelationReader
            .ListAsync(
                owner,
                "list-panes",
                ["-t", Target, "-f", filter.Value],
                cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => RelationReader.ToPane(owner, row))];
    }

    /// <summary>Shows a message on the client viewing this window.</summary>
    /// <param name="request">The message to show.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The printed lines when the request asked for them, else null.</returns>
    /// <exception cref="ArgumentException">
    /// The request asks to redraw the pane, which only a pane can honour.
    /// </exception>
    /// <remarks>
    /// A message with no client to show it on is not a failure, so tmux's
    /// complaint is logged rather than raised.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>?> DisplayMessageAsync(
        DisplayMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UpdatePane)
        {
            throw new ArgumentException(
                "Redrawing while a message is shown is pane-scoped.",
                nameof(request));
        }

        Server owner = RequireOwner("display");
        if (request.TargetClient is not null
            && owner.Version is TmuxVersion version
            && version < TmuxVersion.Parse("3.3a"))
        {
            // tmux 3.2a declares the flag without a value, so naming a client
            // there would silently address a different one.
            throw new TmuxVersionTooLowException(
                "Naming a display-message client requires tmux 3.3a.",
                TmuxVersion.Parse("3.3a"),
                owner.Version ?? default);
        }

        List<string> arguments = ["display-message", "-t", Target];
        if (request.ReturnText)
        {
            arguments.Add("-p");
        }

        if (request.AllFormats)
        {
            arguments.Add("-a");
        }

        if (request.Verbose)
        {
            arguments.Add("-v");
        }

        if (request.NoExpand && RequireLiteralMessages(owner))
        {
            arguments.Add("-l");
        }

        if (request.Notify)
        {
            arguments.Add("-N");
        }

        AddValue(arguments, "-c", request.TargetClient);
        AddValue(
            arguments,
            "-d",
            request.Delay is TimeSpan delay
                ? ((long)delay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                : null);
        AddValue(arguments, "-F", request.Format);
        if (request.Message.Length > 0)
        {
            arguments.Add(request.Message);
        }

        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        if (result.StandardErrorLines.Count > 0
            && owner.Connection?.Options.Logger is ILogger logger)
        {
            LogDisplayMessageRefused(logger, string.Join('\n', result.StandardErrorLines));
        }

        return request.ReturnText ? result.StandardOutputLines : null;
    }

    private static void AddDirection(List<string> arguments, WindowDirection? direction)
    {
        if (direction is WindowDirection value)
        {
            arguments.Add(CommandFlagCatalog.GetWindowDirectionFlag(value));
        }
    }

    private static void AddValue(List<string> arguments, string flag, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            arguments.Add(flag);
            arguments.Add(value);
        }
    }

    private static void AddValue(List<string> arguments, string flag, int? value)
    {
        if (value is int cells)
        {
            arguments.Add(flag);
            arguments.Add(cells.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddEnvironment(
        List<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return;
        }

        foreach ((string key, string value) in environment)
        {
            arguments.Add("-e");
            arguments.Add($"{key}={value}");
        }
    }

    private static bool Supports(Server owner, string capability) =>
        owner.Version is TmuxVersion version
        && TmuxCapabilities.IsSupported(version, capability);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "literal message flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogLiteralUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "split appearance flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogSplitAppearanceUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "empty split flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogSplitEmptyUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "tmux refused to display the message: {TmuxError}")]
    private static partial void LogDisplayMessageRefused(ILogger logger, string tmuxError);

    private static void Warn(Server owner, Action<ILogger, string?> log)
    {
        if (owner.Connection?.Options.Logger is ILogger logger)
        {
            log(logger, owner.RawVersion);
        }
    }

    // The version comes from state captured when the handle materialized, so
    // gating costs no extra tmux command and the call still dispatches once.
    private static bool RequireLiteralMessages(Server owner)
    {
        if (Supports(owner, DisplayMessageLiteralCapability))
        {
            return true;
        }

        Warn(owner, LogLiteralUnsupported);
        return false;
    }

    private void AddSplitAppearance(List<string> arguments, SplitPaneRequest options)
    {
        Server owner = RequireOwner("panes");
        if (options.Empty)
        {
            if (Supports(owner, SplitWindowEmptyCapability))
            {
                arguments.Add("-E");
            }
            else
            {
                Warn(owner, LogSplitEmptyUnsupported);
            }
        }

        bool wantsAppearance = options.Style is not null
            || options.ActiveBorderStyle is not null
            || options.InactiveBorderStyle is not null
            || options.Message is not null
            || options.KeepOpen;
        if (!wantsAppearance)
        {
            return;
        }

        if (!Supports(owner, SplitWindowAppearanceCapability))
        {
            Warn(owner, LogSplitAppearanceUnsupported);
            return;
        }

        AddValue(arguments, "-s", options.Style);
        AddValue(arguments, "-S", options.ActiveBorderStyle);
        AddValue(arguments, "-R", options.InactiveBorderStyle);
        AddValue(arguments, "-m", options.Message);
        if (options.KeepOpen)
        {
            arguments.Add("-k");
        }
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
        if (Uri.IsHexDigit(layout[0])
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

    private string Target => _id.ToString();

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

    private int ReadCapturedInt(string wireName, string relation) =>
        int.TryParse(
            ReadSnapshot(wireName),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int value)
            ? value
            : throw new IncompleteSnapshotException(relation, SnapshotDepth.Windows);

    [UnsupportedOSPlatform("windows")]
    private async Task RunAsync(List<string> arguments, CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, arguments[0]);
    }
}
