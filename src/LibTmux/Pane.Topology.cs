using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Pane
{
    internal List<string> BuildRespawnPaneArguments(RespawnRequest request)
    {
        List<string> arguments = ["respawn-pane", "-t", Target];
        if (request.KillExistingProcess)
        {
            arguments.Add("-k");
        }

        AddValue(arguments, "-c", StartDirectory.Resolve(request.StartDirectory));
        AddEnvironment(arguments, request.Environment);
        if (request.Command is not null)
        {
            arguments.Add(request.Command);
        }

        return arguments;
    }

    /// <summary>Builds the arguments a floating-pane request sends.</summary>
    /// <remarks>
    /// The command itself arrived in tmux 3.7, so the refusal belongs here
    /// rather than beside the dispatch: a chained request that skipped it
    /// would send a command older servers do not have.
    /// </remarks>
    /// <exception cref="TmuxVersionTooLowException">tmux is older than 3.7.</exception>
    internal List<string> BuildNewPaneArguments(NewPaneRequest request)
    {
        Server owner = Server;
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
            request.Target ?? Target,
        ];
        if (!request.Attach)
        {
            arguments.Add("-d");
        }

        AddValue(arguments, "-x", request.Width);
        AddValue(arguments, "-y", request.Height);
        AddValue(arguments, "-X", request.X);
        AddValue(arguments, "-Y", request.Y);
        if (request.Zoom)
        {
            arguments.Add("-Z");
        }

        AddValue(arguments, "-c", StartDirectory.Resolve(request.StartDirectory));
        AddEnvironment(arguments, request.Environment);
        if (request.Empty)
        {
            arguments.Add("-E");
        }

        AddValue(arguments, "-s", request.Style);
        AddValue(arguments, "-S", request.ActiveBorderStyle);
        AddValue(arguments, "-R", request.InactiveBorderStyle);
        AddValue(arguments, "-m", request.Message);
        if (request.KeepOpen)
        {
            arguments.Add("-k");
        }

        if (request.Command is not null)
        {
            arguments.Add(request.Command);
        }

        return arguments;
    }

    /// <summary>Builds the arguments a split request sends.</summary>
    /// <remarks>
    /// Splitting into an empty pane and the appearance flags both arrived in
    /// tmux 3.7, so this stays on the pane that knows which tmux is
    /// answering. It keeps the identifier-printing flags, so a chained split
    /// can say which pane it made.
    /// </remarks>
    internal List<string> BuildSplitArguments(SplitPaneRequest request)
    {
        List<string> arguments =
        [
            "split-window",
            "-P",
            "-F",
            "#{pane_id}",
            "-t",
            // A pane identifier names a pane on its own; composing one with a
            // sub-target would ask tmux for a window that does not exist.
            request.Target ?? Target,
        ];
        foreach (string flag in CommandFlagCatalog.GetPaneDirectionFlags(
            request.Direction ?? PaneDirection.Below))
        {
            arguments.Add(flag);
        }

        // tmux 3.4 misreads the percentage flag, so a percentage rides the size
        // flag instead, which every supported version accepts.
        AddValue(
            arguments,
            "-l",
            request.Percentage is int share
                ? string.Create(CultureInfo.InvariantCulture, $"{share}%")
                : request.Size);
        if (request.FullWindow)
        {
            arguments.Add("-f");
        }

        if (request.Zoom)
        {
            arguments.Add("-Z");
        }

        if (!request.Attach)
        {
            arguments.Add("-d");
        }

        AddValue(arguments, "-c", StartDirectory.Resolve(request.StartDirectory));
        AddEnvironment(arguments, request.Environment);
        AddSplitAppearance(arguments, request);
        if (request.Command is not null)
        {
            arguments.Add(request.Command);
        }

        return arguments;
    }

    /// <summary>Moves this pane out into a window of its own.</summary>
    /// <param name="windowName">The new window's name.</param>
    /// <param name="detach">Whether the new window is left unselected.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The window the pane now lives in.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> BreakAsync(
        string? windowName = null,
        bool detach = true,
        CancellationToken cancellationToken = default)
    {
        Server owner = Server;
        // tmux 3.7 dereferences a null window name here and takes the whole
        // server with it, so that one version always gets a name: the caller's
        // if there is one, otherwise a placeholder that is renamed away after.
        bool needsPlaceholder = Supports(owner, "break_pane_3_7_workaround");
        List<string> arguments = ["break-pane", "-P", "-F", "#{window_id}"];
        if (detach)
        {
            arguments.Add("-d");
        }

        if (windowName is not null)
        {
            arguments.Add("-n");
            arguments.Add(windowName);
        }
        else if (needsPlaceholder)
        {
            arguments.Add("-n");
            arguments.Add("libtmux");
        }

        // The pane goes in -s: break-pane's -t names where the window lands.
        arguments.Add("-s");
        arguments.Add(Target);

        var sequence = new TmuxMutationSequence();
        TmuxCommandResult result = await sequence.MutateAsync(
                () => _commandDispatcher.ExecuteAsync(arguments, cancellationToken),
                static value => TmuxCommandFailure.ThrowIfFailed(value, "break-pane"))
            .ConfigureAwait(false);
        WindowId created = sequence.Observe(() =>
            result.StandardOutputLines.Count > 0
                && WindowId.TryParse(result.StandardOutputLines[0], out WindowId parsed)
                    ? parsed
                    : throw new InvalidDataException("tmux reported no new window identifier."));

        // On that same version tmux keeps the name it was given only some of
        // the time, so a caller who asked for one gets it set explicitly.
        if (windowName is not null && needsPlaceholder)
        {
            await sequence.MutateAsync(
                    () => RunAsync(
                        ["rename-window", "-t", created.ToString(), windowName],
                        cancellationToken))
                .ConfigureAwait(false);
        }

        IReadOnlyList<Window> windows = await sequence
            .ObserveAsync(() => owner.GetWindowsAsync(cancellationToken))
            .ConfigureAwait(false);
        return sequence.Observe(() =>
            windows.FirstOrDefault(window => window.Id == created)
                ?? throw new TmuxObjectNotFoundException(
                    $"tmux did not report the created window '{created}'.",
                    created.ToString()));
    }

    /// <summary>Splits this pane.</summary>
    /// <param name="request">How to split.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The created pane.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> SplitAsync(
        SplitPaneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        SplitPaneRequest options = request ?? new SplitPaneRequest();
        List<string> arguments = BuildSplitArguments(options);

        return await CreatePaneFromAsync(arguments, "split-window", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Creates a floating pane against this one.</summary>
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
        List<string> arguments = BuildNewPaneArguments(options);

        return await CreatePaneFromAsync(arguments, "new-pane", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Joins this pane into another window.</summary>
    /// <param name="request">Where the pane lands.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task JoinAsync(
        MovePaneRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(BuildRehomeArguments("join-pane", request), cancellationToken);

    /// <summary>Moves this pane to another position.</summary>
    /// <param name="request">Where the pane lands.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task MoveAsync(
        MovePaneRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(BuildRehomeArguments("move-pane", request), cancellationToken);

    /// <summary>Swaps this pane with another.</summary>
    /// <param name="request">Which pane to swap with.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task SwapAsync(
        SwapPaneRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildSwapPaneArguments(request);
        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Stops this pane.</summary>
    /// <param name="allExcept">Whether every other pane in the window is stopped instead.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task KillAsync(bool allExcept = false, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["kill-pane"];
        if (allExcept)
        {
            arguments.Add("-a");
        }

        arguments.Add("-t");
        arguments.Add(Target);
        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Restarts the command running in this pane.</summary>
    /// <param name="request">What to respawn, or null to reuse the original.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// tmux refuses to respawn a pane that is still running unless the request
    /// kills it first.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task RespawnAsync(
        RespawnRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        RespawnRequest options = request ?? new RespawnRequest();
        List<string> arguments = BuildRespawnPaneArguments(options);
        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Resizes this pane.</summary>
    /// <param name="request">The size to apply.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the new size.</returns>
    /// <remarks>
    /// tmux clamps a size that does not fit rather than refusing it, so the
    /// result may differ from what was asked for.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> ResizeAsync(
        ResizePaneRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildResizePaneArguments(request);

        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(arguments, cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Sets this pane's width.</summary>
    /// <param name="width">The width in cells.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the new size.</returns>
    [UnsupportedOSPlatform("windows")]
    public Task<Pane> SetWidthAsync(int width, CancellationToken cancellationToken = default) =>
        ResizeAsync(
            new ResizePaneRequest(width: width.ToString(CultureInfo.InvariantCulture)),
            cancellationToken);

    /// <summary>Sets this pane's height.</summary>
    /// <param name="height">The height in cells.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the new size.</returns>
    [UnsupportedOSPlatform("windows")]
    public Task<Pane> SetHeightAsync(int height, CancellationToken cancellationToken = default) =>
        ResizeAsync(
            new ResizePaneRequest(height: height.ToString(CultureInfo.InvariantCulture)),
            cancellationToken);

    /// <summary>Sets this pane's title.</summary>
    /// <param name="title">The new title.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the new title.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> SetTitleAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["select-pane", "-t", Target, "-T", title], cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Selects this pane.</summary>
    /// <param name="request">How to select it.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the state afterwards.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> SelectAsync(
        SelectPaneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        SelectPaneRequest options = request ?? new SelectPaneRequest();
        List<string> arguments = BuildSelectPaneArguments(options);

        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(arguments, cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    internal List<string> BuildSwapPaneArguments(SwapPaneRequest request)
    {
        List<string> arguments = ["swap-pane", "-t", Target];
        if (request.Detach)
        {
            arguments.Add("-d");
        }

        if (request.Direction is PaneSwapDirection direction)
        {
            arguments.Add(direction == PaneSwapDirection.Up ? "-U" : "-D");
        }

        if (request.KeepZoom)
        {
            arguments.Add("-Z");
        }

        AddValue(arguments, "-s", request.Target);

        return arguments;
    }

    internal List<string> BuildResizePaneArguments(ResizePaneRequest request)
    {
        List<string> arguments = ["resize-pane", "-t", Target];
        if (request.Direction is ResizeDirection direction)
        {
            arguments.Add(CommandFlagCatalog.GetResizeDirectionFlag(direction));
        }

        AddValue(arguments, "-x", request.Width);
        AddValue(arguments, "-y", request.Height);
        if (request.Zoom)
        {
            arguments.Add("-Z");
        }

        if (request.Mouse)
        {
            arguments.Add("-M");
        }

        if (request.TrimBelow)
        {
            arguments.Add("-T");
        }

        // tmux takes the adjustment as the trailing positional; as a flag value
        // it would be read as a second argument and refused.
        if (request.Adjustment is int adjustment)
        {
            arguments.Add(adjustment.ToString(CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    internal List<string> BuildSelectPaneArguments(SelectPaneRequest options)
    {
        List<string> arguments = ["select-pane", "-t", Target];
        string? directionFlag = options.Direction switch
        {
            PaneSelectDirection.Up => "-U",
            PaneSelectDirection.Down => "-D",
            PaneSelectDirection.Left => "-L",
            PaneSelectDirection.Right => "-R",
            PaneSelectDirection.Last => "-l",
            _ => null,
        };
        if (directionFlag is not null)
        {
            arguments.Add(directionFlag);
        }

        // Asking for the last pane by direction and by flag is the same
        // request, and tmux only needs telling once.
        if (options.Last && directionFlag != "-l")
        {
            arguments.Add("-l");
        }

        if (options.KeepZoom)
        {
            arguments.Add("-Z");
        }

        if (options.Mark is bool mark)
        {
            arguments.Add(mark ? "-m" : "-M");
        }

        if (options.InputEnabled is bool input)
        {
            arguments.Add(input ? "-e" : "-d");
        }

        return arguments;
    }

    private void AddSplitAppearance(List<string> arguments, SplitPaneRequest options)
    {
        if (options.Empty)
        {
            if (Requires(SplitEmptyCapability, LogSplitEmptyUnsupported))
            {
                arguments.Add("-E");
            }
        }

        bool wantsAppearance = options.Style is not null
            || options.ActiveBorderStyle is not null
            || options.InactiveBorderStyle is not null
            || options.Message is not null
            || options.KeepOpen;
        if (!wantsAppearance || !Requires(SplitAppearanceCapability, LogSplitAppearanceUnsupported))
        {
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

    // move-pane and join-pane both take the pane as -s and where it lands as
    // -t, which is the opposite way round from every other pane command.
    internal List<string> BuildRehomeArguments(string subcommand, MovePaneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments =
        [
            subcommand,
            request.Direction is PaneDirection.Above or PaneDirection.Below ? "-v" : "-h",
        ];
        if (request.Detach)
        {
            arguments.Add("-d");
        }

        if (request.FullWindow)
        {
            arguments.Add("-f");
        }

        // A percentage flag exists but is broken from 3.4 through 3.6, so a
        // size of any shape rides the one flag that works everywhere.
        AddValue(arguments, "-l", request.Size);
        if (request.Before || request.Direction is PaneDirection.Above or PaneDirection.Left)
        {
            arguments.Add("-b");
        }

        arguments.Add("-s");
        arguments.Add(Target);
        arguments.Add("-t");
        arguments.Add(request.Target);
        return arguments;
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<Pane> CreatePaneFromAsync(
        List<string> arguments,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var sequence = new TmuxMutationSequence();
        TmuxCommandResult result = await sequence.MutateAsync(
                () => _commandDispatcher.ExecuteAsync(arguments, cancellationToken),
                value => TmuxCommandFailure.ThrowIfFailed(value, subcommand))
            .ConfigureAwait(false);
        PaneId created = sequence.Observe(() =>
            result.StandardOutputLines.Count > 0
                && PaneId.TryParse(result.StandardOutputLines[0], out PaneId parsed)
                    ? parsed
                    : throw new InvalidDataException("tmux reported no new pane identifier."));

        Server owner = sequence.Observe(() => Server);
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await sequence
            .ObserveAsync(() => RelationReader.ListAsync(
                owner,
                "list-panes",
                ["-a"],
                cancellationToken))
            .ConfigureAwait(false);
        return sequence.Observe(() =>
            rows.Select(row => RelationReader.ToPane(owner, row))
                    .FirstOrDefault(pane => pane.Id == created)
                ?? throw new TmuxObjectNotFoundException(
                    $"tmux did not report the created pane '{created}'.",
                    created.ToString()));
    }
}
