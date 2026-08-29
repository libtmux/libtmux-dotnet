using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>Names which way a window's panes rotate.</summary>
public enum WindowRotationDirection
{
    /// <summary>Rotate panes towards the top of the window.</summary>
    Up = 0,

    /// <summary>Rotate panes towards the bottom of the window.</summary>
    Down = 1,
}
// Mutates a window's lifecycle and returns a replacement when the handle remains valid.
public sealed partial class Window
{
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
}
