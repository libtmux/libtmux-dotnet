using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux;

// Pane mutations return replacements when a truthful handle remains; destructive
// or re-homing operations do not.
public sealed partial class Pane
{
    private const string CaptureTrimCapability = "capture_pane_trim_trailing";
    private const string ChooseTreeSortTimeCapability = "choose_tree_sort_time";
    private const string CaptureModeScreenCapability = "capture_pane_mode_screen";
    private const string CaptureMetadataCapability = "capture_pane_3_7_metadata";
    private const string ClearHistoryHyperlinksCapability = "clear_history_hyperlinks";
    private const string CopyModePageDownCapability = "copy_mode_page_down";
    private const string DisplayMessageLiteralCapability = "display_message_literal";
    private const string DisplayMessageUpdatePaneCapability = "display_message_update_pane";
    private const string PopupOptionsCapability = "display_popup_3_3_options";
    private const string PopupKeyPolicyCapability = "display_popup_3_6_key_policy";
    private const string PasteRawBytesCapability = "paste_buffer_no_vis";
    private const string SendKeysClientCapability = "send_keys_client_keys";
    private const string SplitAppearanceCapability = "split_window_appearance";
    private const string SplitEmptyCapability = "split_window_empty";
    private const string NewPaneCommandCapability = "new_pane_command";

    /// <summary>Gets whether the pane touches the top of its window.</summary>
    public bool AtTop => ReadSnapshot("pane_at_top") == "1";

    /// <summary>Gets whether the pane touches the bottom of its window.</summary>
    public bool AtBottom => ReadSnapshot("pane_at_bottom") == "1";

    /// <summary>Gets whether the pane touches the left of its window.</summary>
    public bool AtLeft => ReadSnapshot("pane_at_left") == "1";

    /// <summary>Gets whether the pane touches the right of its window.</summary>
    public bool AtRight => ReadSnapshot("pane_at_right") == "1";

    /// <summary>Gets the pane height captured with this handle.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The pane was resolved by identifier rather than materialized.
    /// </exception>
    public int Height => ReadCapturedInt("pane_height", "height");

    /// <summary>Gets the pane width captured with this handle.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The pane was resolved by identifier rather than materialized.
    /// </exception>
    public int Width => ReadCapturedInt("pane_width", "width");

    /// <summary>Gets the index this pane holds in its window.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The pane was resolved by identifier rather than materialized.
    /// </exception>
    public int Index => ReadCapturedInt("pane_index", "index");

    /// <summary>Gets the pane title captured with this handle.</summary>
    public string? Title => ReadSnapshot("pane_title");

    /// <summary>Reads the pane's contents.</summary>
    /// <param name="request">What to capture.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The captured lines.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<string>> CaptureAsync(
        CapturePaneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = BuildCaptureArguments(["-p"], request ?? new CapturePaneRequest());
        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "capture-pane");
        return result.StandardOutputLines;
    }

    /// <summary>Captures the pane's contents into a tmux buffer.</summary>
    /// <param name="bufferName">The buffer to write.</param>
    /// <param name="request">What to capture.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task CaptureToBufferAsync(
        string bufferName,
        CapturePaneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bufferName);
        // tmux checks for printing before buffering and takes the first it
        // finds, so a buffer name only lands when nothing asks it to print.
        return RunAsync(
            BuildCaptureArguments(["-b", bufferName], request ?? new CapturePaneRequest()),
            cancellationToken);
    }

    internal List<string> BuildSendKeysArguments(SendKeysRequest request)
    {
        List<string> arguments = ["send-keys", "-t", Target];
        if (request.Reset)
        {
            arguments.Add("-R");
        }

        if (request.ExpandFormats)
        {
            arguments.Add("-F");
        }

        if (request.HexKeys)
        {
            arguments.Add("-H");
        }

        AddClientKeys(arguments, request);
        if (request.Literal)
        {
            arguments.Add("-l");
        }

        AddValue(arguments, "-N", request.Repeat);
        if (request.CopyModeCommand is not null)
        {
            arguments.Add("-X");
            arguments.Add(request.CopyModeCommand);
        }
        else if (request.Text is not null)
        {
            // There is no tmux flag for keeping a line out of shell history;
            // a leading space is the shell convention that does it.
            arguments.Add(request.SuppressHistory ? $" {request.Text}" : request.Text);
        }

        return arguments;
    }

    /// <summary>Sends keys to the pane.</summary>
    /// <param name="request">What to send.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <exception cref="ArgumentException">The request sends nothing.</exception>
    /// <exception cref="LibTmuxException">
    /// Text was sent, but a requested Enter failed. Its dispatch state is
    /// unknown, so the whole request must not be retried.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public async Task SendKeysAsync(
        SendKeysRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text is null
            && request.CopyModeCommand is null
            && !request.Reset
            && request.Repeat is null)
        {
            throw new ArgumentException("The request sends no keys.", nameof(request));
        }

        List<string> arguments = BuildSendKeysArguments(request);
        var sequence = new TmuxMutationSequence(
            "The text was sent, but Enter failed. The pane may already have "
            + "acted on the text; do not retry the whole request.");
        await sequence.MutateAsync(() => RunAsync(arguments, cancellationToken))
            .ConfigureAwait(false);

        // Enter rides in its own command: appended to a literal send it would
        // type the five characters of its name instead of pressing the key.
        if (request.CopyModeCommand is null && request.Text is not null && request.Enter)
        {
            await sequence.MutateAsync(
                    () => RunAsync(["send-keys", "-t", Target, "Enter"], cancellationToken))
                .ConfigureAwait(false);
        }
    }

    /// <summary>Types text into the pane.</summary>
    /// <param name="text">The text to type.</param>
    /// <param name="enter">Whether Enter follows the text.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <exception cref="LibTmuxException">
    /// Text was sent, but Enter failed. Its dispatch state is unknown, so the
    /// whole request must not be retried.
    /// </exception>
    [UnsupportedOSPlatform("windows")]
    public Task SendTextAsync(
        string text,
        bool enter = true,
        CancellationToken cancellationToken = default) =>
        SendKeysAsync(new SendKeysRequest(text, enter, literal: true), cancellationToken);

    /// <summary>Sends the configured prefix key to the pane.</summary>
    /// <param name="secondary">Whether the secondary prefix is sent.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task SendPrefixAsync(
        bool secondary = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["send-prefix", "-t", Target];
        if (secondary)
        {
            arguments.Add("-2");
        }

        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Presses Enter in the pane.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the state afterwards.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> EnterAsync(CancellationToken cancellationToken = default)
    {
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["send-keys", "-t", Target, "Enter"], cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Clears the pane by running the shell's reset.</summary>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>A replacement handle carrying the state afterwards.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> ClearAsync(CancellationToken cancellationToken = default)
    {
        return await TmuxMutationSequence.RunAsync(
                () => SendKeysAsync(new SendKeysRequest("reset"), cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Resets the pane's terminal state and drops its history.</summary>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>A replacement handle carrying the state afterwards.</returns>
    /// <remarks>
    /// Python groups the two tmux commands so nothing runs between them. This
    /// dispatches them in turn, because the transport carries one command per
    /// call and a trailing separator would reach tmux as data.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> ResetAsync(CancellationToken cancellationToken = default)
    {
        var sequence = new TmuxMutationSequence();
        await sequence.MutateAsync(
                () => RunAsync(["send-keys", "-t", Target, "-R"], cancellationToken))
            .ConfigureAwait(false);
        await sequence.MutateAsync(
                () => RunAsync(["clear-history", "-t", Target], cancellationToken))
            .ConfigureAwait(false);
        return await sequence.ObserveAsync(() => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Drops the pane's scrollback history.</summary>
    /// <param name="resetHyperlinks">Whether stored hyperlinks are dropped too.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task ClearHistoryAsync(
        bool resetHyperlinks = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["clear-history", "-t", Target];
        if (resetHyperlinks && Requires(ClearHistoryHyperlinksCapability, LogHyperlinksUnsupported))
        {
            arguments.Add("-H");
        }

        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Pipes the pane's input or output through a command.</summary>
    /// <param name="request">What to pipe.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task PipeAsync(
        PipePaneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        PipePaneRequest options = request ?? new PipePaneRequest();
        List<string> arguments = BuildPipePaneArguments(options);
        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Builds the arguments a popup request sends.</summary>
    /// <remarks>
    /// Popup options arrived in tmux 3.3 and the key policy in 3.6, so this
    /// stays on the pane that knows which tmux is answering.
    /// </remarks>
    internal List<string> BuildDisplayPopupArguments(DisplayPopupRequest request)
    {
        List<string> arguments = ["display-popup", "-t", Target];
        if (request.CloseExisting)
        {
            arguments.Add("-C");
        }

        AddValue(arguments, "-c", request.TargetClient);
        if (request.CloseMode is PopupCloseMode close)
        {
            // tmux reads the flag twice to mean "only on success", which is the
            // one place a flag is repeated deliberately.
            arguments.Add("-E");
            if (close == PopupCloseMode.SuccessfulExit)
            {
                arguments.Add("-E");
            }
        }

        AddValue(arguments, "-w", request.Width);
        AddValue(arguments, "-h", request.Height);
        AddValue(arguments, "-x", request.X);
        AddValue(arguments, "-y", request.Y);
        AddValue(arguments, "-d", StartDirectory.Resolve(request.StartDirectory));
        AddPopupOptions(arguments, request);
        AddPopupKeyPolicy(arguments, request);
        if (request.Command is not null)
        {
            arguments.Add(request.Command);
        }

        return arguments;
    }

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

    /// <summary>Builds the arguments a chooser request sends.</summary>
    /// <remarks>
    /// tmux 3.7 dropped the activity-time sort order and rejects it by name,
    /// so this stays on the pane that knows which tmux is answering.
    /// </remarks>
    internal List<string> BuildChooseTreeArguments(ChooseTreeRequest request)
    {
        List<string> arguments = ["choose-tree", "-t", Target];
        if (request.SessionsCollapsed)
        {
            arguments.Add("-s");
        }

        if (request.WindowsCollapsed)
        {
            arguments.Add("-w");
        }

        if (request.Zoom)
        {
            arguments.Add("-Z");
        }

        if (request.Reverse)
        {
            arguments.Add("-r");
        }

        AddValue(arguments, "-F", request.Format);
        AddValue(arguments, "-f", request.NativeFilter?.Value);
        // tmux 3.7 dropped the activity-time order and rejects it by name, so
        // sending it there fails the whole command rather than sorting badly.
        // Omitting leaves the chooser's default order, which is the same thing
        // a caller who never asked would have got.
        ChooseTreeSort? sort = request.Sort == ChooseTreeSort.Time
            && !Requires(ChooseTreeSortTimeCapability, LogChooseTreeSortTime)
                ? null
                : request.Sort;
        AddValue(arguments, "-O", SortOrder(sort));

        return arguments;
    }

    /// <summary>Builds the arguments a copy-mode request sends.</summary>
    /// <remarks>
    /// Paging down on entry arrived in tmux 3.5, so this stays on the pane
    /// that knows which tmux is answering.
    /// </remarks>
    internal List<string> BuildCopyModeArguments(CopyModeRequest request)
    {
        List<string> arguments = ["copy-mode", "-t", Target];
        if (request.ScrollUp)
        {
            arguments.Add("-u");
        }

        if (request.ExitOnBottom)
        {
            arguments.Add("-e");
        }

        if (request.MouseDrag)
        {
            arguments.Add("-M");
        }

        if (request.PageDown && Requires(CopyModePageDownCapability, LogPageDownUnsupported))
        {
            arguments.Add("-d");
        }

        AddValue(arguments, "-s", request.SourcePane);
        if (request.Cancel)
        {
            arguments.Add("-q");
        }

        return arguments;
    }

    /// <summary>Builds the arguments a paste request sends.</summary>
    /// <remarks>
    /// Pasting raw bytes arrived in tmux 3.7, so this stays on the pane that
    /// knows which tmux is answering.
    /// </remarks>
    internal List<string> BuildPasteBufferArguments(PasteBufferRequest request)
    {
        List<string> arguments = ["paste-buffer", "-t", Target];
        if (request.DeleteAfter)
        {
            arguments.Add("-d");
        }

        if (request.UseLineFeedSeparator)
        {
            arguments.Add("-r");
        }

        if (request.Bracketed)
        {
            arguments.Add("-p");
        }

        AddValue(arguments, "-b", request.Name);
        AddValue(arguments, "-s", request.Separator);
        if (request.RawBytes && Requires(PasteRawBytesCapability, LogRawPasteUnsupported))
        {
            arguments.Add("-S");
        }

        return arguments;
    }

    /// <summary>Pastes a tmux buffer into the pane.</summary>
    /// <param name="request">Which buffer and how.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task PasteBufferAsync(
        PasteBufferRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        PasteBufferRequest options = request ?? new PasteBufferRequest();
        List<string> arguments = BuildPasteBufferArguments(options);
        return RunAsync(arguments, cancellationToken);
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

    internal List<string> BuildPipePaneArguments(PipePaneRequest request)
    {
        List<string> arguments = ["pipe-pane", "-t", Target];
        if (request.OutputOnly)
        {
            arguments.Add("-O");
        }

        if (request.InputOnly)
        {
            arguments.Add("-I");
        }

        if (request.Toggle)
        {
            arguments.Add("-o");
        }

        if (request.Command is not null)
        {
            arguments.Add(request.Command);
        }

        return arguments;
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

    internal List<string> BuildFindWindowArguments(FindWindowRequest request)
    {
        List<string> arguments = ["find-window", "-t", Target];
        if (request.MatchContent)
        {
            arguments.Add("-C");
        }

        if (request.IgnoreCase)
        {
            arguments.Add("-i");
        }

        if (request.MatchName)
        {
            arguments.Add("-N");
        }

        if (request.Regex)
        {
            arguments.Add("-r");
        }

        if (request.MatchTitle)
        {
            arguments.Add("-T");
        }

        arguments.Add(request.Pattern);

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

    /// <summary>Re-reads this pane from tmux.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying current state.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Pane> RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Listing by -t fails loudly on a pane that is already gone, which
        // would report a command failure where the pane is simply missing.
        Server owner = Server;
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await RelationReader
            .ListAsync(owner, "list-panes", ["-a"], cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => RelationReader.ToPane(owner, row))
                .FirstOrDefault(pane => pane.Id == _id)
            ?? throw new TmuxObjectNotFoundException(
                $"tmux no longer has pane '{_id}'.",
                _id.ToString());
    }

    /// <summary>Shows a popup over the client viewing this pane.</summary>
    /// <param name="request">What the popup shows.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// tmux waits for the popup to close before answering, so a popup whose
    /// command never exits keeps this call waiting until it is canceled.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task DisplayPopupAsync(
        DisplayPopupRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        DisplayPopupRequest options = request ?? new DisplayPopupRequest();
        List<string> arguments = BuildDisplayPopupArguments(options);
        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Shows the pane numbers on every client.</summary>
    /// <param name="duration">How long the numbers stay up.</param>
    /// <param name="noSelect">Whether pressing a number does not select a pane.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// tmux takes no pane here: the command's target names a client, so this
    /// shows the numbers wherever the server has clients.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task DisplayPaneNumbersAsync(
        TimeSpan? duration = null,
        bool noSelect = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["display-panes"];
        AddValue(
            arguments,
            "-d",
            duration is TimeSpan window
                ? ((long)window.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                : null);
        if (noSelect)
        {
            arguments.Add("-N");
        }

        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Shows a message on the client viewing this pane.</summary>
    /// <param name="request">The message to show.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The printed lines when the request asked for them, else null.</returns>
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
        Server owner = Server;
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

        if (request.NoExpand && Requires(DisplayMessageLiteralCapability, LogLiteralUnsupported))
        {
            arguments.Add("-l");
        }

        if (request.Notify)
        {
            arguments.Add("-N");
        }

        if (request.UpdatePane
            && Requires(DisplayMessageUpdatePaneCapability, LogUpdatePaneUnsupported))
        {
            arguments.Add("-C");
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

    /// <summary>Puts the pane into copy mode.</summary>
    /// <param name="request">How to enter it.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task EnterCopyModeAsync(
        CopyModeRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        CopyModeRequest options = request ?? new CopyModeRequest();
        List<string> arguments = BuildCopyModeArguments(options);
        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Puts the pane into clock mode.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task EnterClockModeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["clock-mode", "-t", Target], cancellationToken);

    /// <summary>Puts the pane into customize mode.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task EnterCustomizeModeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["customize-mode", "-t", Target], cancellationToken);

    /// <summary>Opens the buffer chooser in this pane.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>tmux does nothing, successfully, when there are no buffers.</remarks>
    [UnsupportedOSPlatform("windows")]
    public Task ChooseBufferAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["choose-buffer", "-t", Target], cancellationToken);

    /// <summary>Opens the client chooser in this pane.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task ChooseClientAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["choose-client", "-t", Target], cancellationToken);

    /// <summary>Opens the session tree chooser in this pane.</summary>
    /// <param name="request">How the tree is shown.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task ChooseTreeAsync(
        ChooseTreeRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ChooseTreeRequest options = request ?? new ChooseTreeRequest();
        List<string> arguments = BuildChooseTreeArguments(options);
        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Opens the window finder in this pane.</summary>
    /// <param name="request">What to look for.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task FindWindowAsync(
        FindWindowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> arguments = BuildFindWindowArguments(request);
        return RunAsync(arguments, cancellationToken);
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

    private static string? SortOrder(ChooseTreeSort? sort) => sort switch
    {
        ChooseTreeSort.Index => "index",
        ChooseTreeSort.Name => "name",
        ChooseTreeSort.Time => "time",
        ChooseTreeSort.Size => "size",
        _ => null,
    };

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "trailing-space trim flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogTrimUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "mode-screen capture flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogModeScreenUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Warning,
        Message = "capture metadata flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogCaptureMetadataUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Warning,
        Message = "hyperlink reset flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogHyperlinksUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "copy-mode page-down flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogPageDownUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Warning,
        Message = "literal message flag omitted, tmux {TmuxVersion} will expand the message")]
    private static partial void LogLiteralUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Warning,
        Message = "pane redraw flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogUpdatePaneUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "popup appearance flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogPopupOptionsUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Warning,
        Message = "popup key flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogPopupKeyPolicyUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 15,
        Level = LogLevel.Warning,
        Message = "raw paste flag omitted, tmux {TmuxVersion} already pastes raw bytes")]
    private static partial void LogRawPasteUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 16,
        Level = LogLevel.Warning,
        Message = "send-keys client flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogClientKeysUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 17,
        Level = LogLevel.Warning,
        Message = "split appearance flags omitted, tmux {TmuxVersion} does not carry them")]
    private static partial void LogSplitAppearanceUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 18,
        Level = LogLevel.Warning,
        Message = "empty split flag omitted, tmux {TmuxVersion} will spawn a shell instead")]
    private static partial void LogSplitEmptyUnsupported(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Warning,
        Message = "activity-time sort order omitted, tmux {TmuxVersion} dropped it")]
    private static partial void LogChooseTreeSortTime(ILogger logger, string? tmuxVersion);

    [LoggerMessage(
        EventId = 19,
        Level = LogLevel.Warning,
        Message = "tmux refused to display the message: {TmuxError}")]
    private static partial void LogDisplayMessageRefused(ILogger logger, string tmuxError);

    // The version comes from state captured when the handle materialized, so
    // gating costs no extra tmux command and the call still dispatches once.
    private bool Requires(string capability, Action<ILogger, string?> log)
    {
        Server owner = Server;
        if (Supports(owner, capability))
        {
            return true;
        }

        if (owner.Connection?.Options.Logger is ILogger logger)
        {
            log(logger, owner.RawVersion);
        }

        return false;
    }

    internal List<string> BuildCaptureArguments(List<string> head, CapturePaneRequest options)
    {
        List<string> arguments = ["capture-pane", "-t", Target, .. head];
        AddValue(arguments, "-S", Position(options.StartLine));
        AddValue(arguments, "-E", Position(options.EndLine));
        if (options.EscapeSequences)
        {
            arguments.Add("-e");
        }

        if (options.EscapeNonPrintable)
        {
            arguments.Add("-C");
        }

        if (options.JoinWrappedLines)
        {
            arguments.Add("-J");
        }

        if (options.PreserveTrailingSpaces)
        {
            arguments.Add("-N");
        }

        if (options.TrimTrailingSpaces && Requires(CaptureTrimCapability, LogTrimUnsupported))
        {
            arguments.Add("-T");
        }

        if (options.AlternateScreen)
        {
            arguments.Add("-a");
        }

        if (options.Quiet)
        {
            arguments.Add("-q");
        }

        if (options.ModeScreen && Requires(CaptureModeScreenCapability, LogModeScreenUnsupported))
        {
            arguments.Add("-M");
        }

        if (options.Pending)
        {
            arguments.Add("-P");
        }

        AddCaptureMetadata(arguments, options);
        return arguments;

        static string? Position(CapturePanePosition? position) => position is null
            ? null
            : position.Value.LineNumber?.ToString(CultureInfo.InvariantCulture) ?? "-";
    }

    private void AddCaptureMetadata(List<string> arguments, CapturePaneRequest options)
    {
        if (!options.Hyperlinks && !options.LineNumbers && !options.LineFlags)
        {
            return;
        }

        if (!Requires(CaptureMetadataCapability, LogCaptureMetadataUnsupported))
        {
            return;
        }

        if (options.Hyperlinks)
        {
            arguments.Add("-H");
        }

        if (options.LineNumbers)
        {
            arguments.Add("-L");
        }

        if (options.LineFlags)
        {
            arguments.Add("-F");
        }
    }

    private void AddClientKeys(List<string> arguments, SendKeysRequest request)
    {
        if (!request.KeyName && request.TargetClient is null)
        {
            return;
        }

        if (!Requires(SendKeysClientCapability, LogClientKeysUnsupported))
        {
            return;
        }

        if (request.KeyName)
        {
            arguments.Add("-K");
        }

        AddValue(arguments, "-c", request.TargetClient);
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

    private void AddPopupOptions(List<string> arguments, DisplayPopupRequest options)
    {
        bool wants = options.Title is not null
            || options.BorderLines is not null
            || options.Style is not null
            || options.BorderStyle is not null
            || options.Environment is not null
            || options.NoBorder;
        if (!wants || !Requires(PopupOptionsCapability, LogPopupOptionsUnsupported))
        {
            return;
        }

        AddValue(arguments, "-T", options.Title);
        AddValue(arguments, "-b", options.BorderLines);
        AddValue(arguments, "-s", options.Style);
        AddValue(arguments, "-S", options.BorderStyle);
        AddEnvironment(arguments, options.Environment);
        if (options.NoBorder)
        {
            arguments.Add("-B");
        }
    }

    private void AddPopupKeyPolicy(List<string> arguments, DisplayPopupRequest options)
    {
        if (!options.CloseOnAnyKey && !options.NoKeys)
        {
            return;
        }

        if (!Requires(PopupKeyPolicyCapability, LogPopupKeyPolicyUnsupported))
        {
            return;
        }

        if (options.CloseOnAnyKey)
        {
            arguments.Add("-k");
        }

        if (options.NoKeys)
        {
            arguments.Add("-N");
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

    private string Target => _id.ToString();

    private int ReadCapturedInt(string wireName, string relation) =>
        int.TryParse(
            ReadSnapshot(wireName),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int value)
            ? value
            : throw new IncompleteSnapshotException(relation, SnapshotDepth.Server);

    [UnsupportedOSPlatform("windows")]
    private async Task RunAsync(List<string> arguments, CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, arguments[0]);
    }
}
