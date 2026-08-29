using System.Runtime.Versioning;

namespace LibTmux;

// Builds and executes pane requests.
public static partial class TmuxChaining
{
    /// <summary>Returns a key request as one tmux command for a pane.</summary>
    /// <param name="request">The keys to send.</param>
    /// <param name="pane">The pane that receives them.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this SendKeysRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);

        // The pane ID travels into the chain as plain text, so RequiredGeneration
        // pins it: after a restart, that ID could name a different pane.
        return Command([.. pane.BuildSendKeysArguments(request)]) with
        {
            RequiredGeneration = pane.Generation,
        };
    }

    /// <summary>Returns a pane-selection request as one tmux command.</summary>
    /// <param name="request">Which pane to select, and how.</param>
    /// <param name="pane">The pane the selection is relative to.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this SelectPaneRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildSelectPaneArguments(request)]);
    }

    /// <summary>Runs a pane-selection request on its own.</summary>
    /// <param name="request">Which pane to select, and how.</param>
    /// <param name="pane">The pane the selection is relative to.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary selection.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this SelectPaneRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a pane-resize request as one tmux command.</summary>
    /// <param name="request">How to resize.</param>
    /// <param name="pane">The pane being resized.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this ResizePaneRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildResizePaneArguments(request)]);
    }

    /// <summary>Runs a pane-resize request on its own.</summary>
    /// <param name="request">How to resize.</param>
    /// <param name="pane">The pane being resized.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary resize.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ResizePaneRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a window-search request as one tmux command.</summary>
    /// <param name="request">What to look for.</param>
    /// <param name="pane">The pane the search starts from.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this FindWindowRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildFindWindowArguments(request)]);
    }

    /// <summary>Runs a window-search request on its own.</summary>
    /// <param name="request">What to look for.</param>
    /// <param name="pane">The pane the search starts from.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary search.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this FindWindowRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a pane-swap request as one tmux command.</summary>
    /// <param name="request">Which pane to swap with, and how.</param>
    /// <param name="pane">The pane being swapped.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this SwapPaneRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildSwapPaneArguments(request)]);
    }

    /// <summary>Runs a pane-swap request on its own.</summary>
    /// <param name="request">Which pane to swap with, and how.</param>
    /// <param name="pane">The pane being swapped.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary swap.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this SwapPaneRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a pane-piping request as one tmux command.</summary>
    /// <param name="request">What to pipe, and which way.</param>
    /// <param name="pane">The pane being piped.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this PipePaneRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildPipePaneArguments(request)]);
    }

    /// <summary>Runs a pane-piping request on its own.</summary>
    /// <param name="request">What to pipe, and which way.</param>
    /// <param name="pane">The pane being piped.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary pipe.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this PipePaneRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a capture request as one tmux command.</summary>
    /// <param name="request">What to capture.</param>
    /// <param name="pane">The pane being captured.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Several capture flags arrived after tmux 3.2a, and the pane is what
    /// knows which tmux is answering, so the command it builds carries only
    /// the flags that server accepts.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this CapturePaneRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildCaptureArguments(["-p"], request)]);
    }

    /// <summary>Runs a capture request on its own.</summary>
    /// <param name="request">What to capture.</param>
    /// <param name="pane">The pane being captured.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is the captured text.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this CapturePaneRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a paste request as one tmux command.</summary>
    /// <param name="request">Which buffer to paste, and how.</param>
    /// <param name="pane">The pane being pasted into.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Pasting raw bytes arrived in tmux 3.7, so the pane decides whether the
    /// built command carries that flag.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this PasteBufferRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildPasteBufferArguments(request)]);
    }

    /// <summary>Runs a paste request on its own.</summary>
    /// <param name="request">Which buffer to paste, and how.</param>
    /// <param name="pane">The pane being pasted into.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary paste.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this PasteBufferRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a popup request as one tmux command.</summary>
    /// <param name="request">What the popup shows, and where.</param>
    /// <param name="pane">The pane the popup belongs to.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Popup options arrived in tmux 3.3 and the key policy in 3.6, so the
    /// pane decides which of them the built command carries.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this DisplayPopupRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildDisplayPopupArguments(request)]);
    }

    /// <summary>Runs a popup request on its own.</summary>
    /// <param name="request">What the popup shows, and where.</param>
    /// <param name="pane">The pane the popup belongs to.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary popup.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this DisplayPopupRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a copy-mode request as one tmux command.</summary>
    /// <param name="request">How to enter copy mode, or whether to leave it.</param>
    /// <param name="pane">The pane entering copy mode.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Paging down on entry arrived in tmux 3.5, so the pane decides whether
    /// the built command carries that flag.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this CopyModeRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildCopyModeArguments(request)]);
    }

    /// <summary>Runs a copy-mode request on its own.</summary>
    /// <param name="request">How to enter copy mode, or whether to leave it.</param>
    /// <param name="pane">The pane entering copy mode.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary entry.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this CopyModeRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a respawn request as one tmux command for a pane.</summary>
    /// <param name="request">What to respawn, and how.</param>
    /// <param name="pane">The pane being respawned.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this RespawnRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildRespawnPaneArguments(request)]);
    }

    /// <summary>Runs a respawn request on its own.</summary>
    /// <param name="request">What to respawn, and how.</param>
    /// <param name="pane">The pane being respawned.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary respawn.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this RespawnRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a chooser request as one tmux command.</summary>
    /// <param name="request">What the chooser shows, and how it is ordered.</param>
    /// <param name="pane">The pane the chooser opens in.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// tmux 3.7 dropped the activity-time sort order and rejects it by name,
    /// so the pane decides whether the built command carries it.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this ChooseTreeRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildChooseTreeArguments(request)]);
    }

    /// <summary>Runs a chooser request on its own.</summary>
    /// <param name="request">What the chooser shows, and how it is ordered.</param>
    /// <param name="pane">The pane the chooser opens in.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary chooser.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this ChooseTreeRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a pane-move request as one tmux command.</summary>
    /// <param name="request">Where the pane goes.</param>
    /// <param name="pane">The pane being moved.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this MovePaneRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildRehomeArguments("move-pane", request)]);
    }

    /// <summary>Runs a pane-move request on its own.</summary>
    /// <param name="request">Where the pane goes.</param>
    /// <param name="pane">The pane being moved.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary move.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this MovePaneRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a split request as one tmux command.</summary>
    /// <param name="request">How to split.</param>
    /// <param name="pane">The pane being split.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// Splitting into an empty pane arrived in tmux 3.7 and the appearance
    /// flags in 3.6, so the pane decides which of them the built command
    /// carries. It prints the new pane's identifier the same way the one-shot
    /// path does.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TmuxCommand ToCommand(this SplitPaneRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildSplitArguments(request)]);
    }

    /// <summary>Runs a split request on its own.</summary>
    /// <param name="request">How to split.</param>
    /// <param name="pane">The pane being split.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which names the created pane.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this SplitPaneRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Returns a floating-pane request as one tmux command.</summary>
    /// <param name="request">How the pane floats.</param>
    /// <param name="pane">The pane the new one is created from.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// The command arrived whole in tmux 3.7, so batching does not soften the
    /// refusal below that: an older server has nothing to send it to.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxVersionTooLowException">tmux is older than 3.7.</exception>
    public static TmuxCommand ToCommand(this NewPaneRequest request, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pane);
        return Command([.. pane.BuildNewPaneArguments(request)]);
    }

    /// <summary>Runs a floating-pane request on its own.</summary>
    /// <param name="request">How the pane floats.</param>
    /// <param name="pane">The pane the new one is created from.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which names the created pane.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this NewPaneRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }

    /// <summary>Runs a key request on its own.</summary>
    /// <param name="request">The keys to send.</param>
    /// <param name="pane">The pane that receives them.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>What tmux printed, which is nothing for an ordinary send.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="TmuxCommandException">tmux reported the command failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public static Task<TmuxCommandResult> ExecuteAsync(
        this SendKeysRequest request,
        Pane pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return pane.Server.Chain().Then(request.ToCommand(pane)).ExecuteAsync(cancellationToken);
    }
}
