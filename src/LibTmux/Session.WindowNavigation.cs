using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Moves between a session's windows and owns ones it creates.
public sealed partial class Session
{
    /// <summary>Selects the window that was last active.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The window that is active afterwards.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> SelectLastWindowAsync(CancellationToken cancellationToken = default)
    {
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["last-window", "-t", _id.ToString()], cancellationToken),
                () => ActiveWindowAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Creates a window in this session and takes ownership of it.</summary>
    /// <param name="request">The window to create.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>A scope that stops the window when disposed.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<OwnedWindowScope> CreateOwnedWindowAsync(
        NewWindowRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var sequence = new TmuxMutationSequence();
        if (request is { SelectExisting: true } or { KillExisting: true })
        {
            throw new ArgumentException("Owned creation cannot select or replace an existing window.", nameof(request));
        }

        Window created = await sequence
            .MutateAsync(() => CreateWindowAsync(request, cancellationToken))
            .ConfigureAwait(false);
        return sequence.Observe(() => new OwnedWindowScope(created));
    }
}

/// <summary>Owns a window and destroys it when disposed.</summary>
public sealed class OwnedWindowScope : IOwnedTmuxResource<Window>
{
    private readonly OwnedCleanup _cleanup;

    [UnsupportedOSPlatform("windows")]
    internal OwnedWindowScope(Window value)
    {
        Value = value;
        _cleanup = new OwnedCleanup(token => OwnedCleanup.DestroyAsync(value.Server, value.Generation, "window", value.Id.ToString(), token));
    }

    /// <summary>Gets the owned window.</summary>
    public Window Value { get; }

    /// <summary>Destroys the window with an independent five-second deadline.</summary>
    /// <returns>The shared cleanup attempt.</returns>
    /// <remarks>
    /// Concurrent calls share an attempt. Failed cleanup is retryable and successful cleanup is idempotent.
    /// Use <see cref="OwnedScope" /> to retain body and teardown failures together.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public ValueTask DisposeAsync() => _cleanup.DisposeAsync();
}
