using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux;

public sealed partial class Pane
{
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

    private static string? SortOrder(ChooseTreeSort? sort) => sort switch
    {
        ChooseTreeSort.Index => "index",
        ChooseTreeSort.Name => "name",
        ChooseTreeSort.Time => "time",
        ChooseTreeSort.Size => "size",
        _ => null,
    };

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
}
