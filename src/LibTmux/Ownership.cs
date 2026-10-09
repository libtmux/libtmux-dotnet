using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>Accepts responsibility for destroying one remote tmux resource.</summary>
/// <typeparam name="T">The borrowed handle exposed by the owner.</typeparam>
/// <remarks>Disposal retains the captured endpoint, daemon generation and object identifier.</remarks>
public interface IOwnedTmuxResource<out T> : IAsyncDisposable
{
    /// <summary>Gets the handle whose remote lifetime this scope owns.</summary>
    public T Value { get; }
}

/// <summary>Runs asynchronous owned scopes while retaining body and cleanup failures.</summary>
public static class OwnedScope
{
    private const string FailureKey = "LibTmux.CleanupFailure";

    /// <summary>Returns the cleanup failure attached to an exception from an owned scope or acquisition.</summary>
    /// <param name="error">The original body or acquisition exception.</param>
    /// <returns>The teardown error, or null when teardown succeeded.</returns>
    public static Exception? CleanupFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Data[FailureKey] as Exception;
    }

    /// <summary>Runs a callback and disposes its owner after success, failure or cancellation.</summary>
    /// <typeparam name="T">The resource handle.</typeparam>
    /// <typeparam name="TResult">The callback result.</typeparam>
    /// <param name="owner">The scope accepting destruction responsibility.</param>
    /// <param name="body">The callback receiving the borrowed handle.</param>
    /// <param name="cancellationToken">Cancels the body; cleanup uses an independent deadline.</param>
    /// <returns>The callback result after successful cleanup.</returns>
    /// <remarks>
    /// The original body exception, including cancellation and its token, propagates.
    /// If teardown also fails, inspect it with <see cref="CleanupFailure(Exception)" />.
    /// A cleanup failure after a successful body propagates on its own. Failed disposal remains retryable.
    /// </remarks>
    public static async Task<TResult> UseAsync<T, TResult>(
        this IOwnedTmuxResource<T> owner,
        Func<T, CancellationToken, Task<TResult>> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(body);
        TResult result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await body(owner.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await PreserveCleanupAsync(failure, () => owner.DisposeAsync().AsTask()).ConfigureAwait(false);
            throw;
        }

        await owner.DisposeAsync().ConfigureAwait(false);
        return result;
    }

    /// <summary>Runs a callback and disposes its owner while retaining both failures.</summary>
    /// <typeparam name="T">The resource handle.</typeparam>
    /// <param name="owner">The scope accepting destruction responsibility.</param>
    /// <param name="body">The callback receiving the borrowed handle.</param>
    /// <param name="cancellationToken">Cancels the body without cancelling cleanup.</param>
    /// <returns>A task that completes after cleanup.</returns>
    public static Task UseAsync<T>(
        this IOwnedTmuxResource<T> owner,
        Func<T, CancellationToken, Task> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return owner.UseAsync(async (value, token) =>
        {
            await body(value, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    internal static async Task PreserveCleanupAsync(Exception failure, Func<Task> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            failure.Data[FailureKey] = CleanupFailure(failure) is { } previous
                ? new AggregateException(previous, cleanupFailure)
                : cleanupFailure;
        }
    }
}

/// <summary>Owns a pane and destroys it when disposed, including after it moves to another window.</summary>
public sealed class OwnedPaneScope : IOwnedTmuxResource<Pane>
{
    private readonly OwnedCleanup _cleanup;

    [UnsupportedOSPlatform("windows")]
    internal OwnedPaneScope(Pane value)
    {
        Value = value;
        _cleanup = new OwnedCleanup(token => OwnedCleanup.DestroyAsync(value.Server, value.Generation, "pane", value.Id.ToString(), token));
    }

    /// <summary>Gets the owned pane.</summary>
    public Pane Value { get; }

    /// <summary>Destroys the pane with an independent five-second deadline.</summary>
    /// <returns>The shared cleanup attempt.</returns>
    /// <remarks>Successful repeated disposal is harmless; a failed attempt can be retried.</remarks>
    [UnsupportedOSPlatform("windows")]
    public ValueTask DisposeAsync() => _cleanup.DisposeAsync();
}

public sealed partial class Session
{
    /// <summary>Accepts destruction responsibility for this session and its windows.</summary>
    /// <param name="cancellationToken">Cancels identity acceptance before ownership transfers.</param>
    /// <returns>An owner retaining this session's ID and daemon generation.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<OwnedSessionScope> AdoptAsync(CancellationToken cancellationToken = default)
    {
        Server owner = await TmuxOwnershipIdentity.CaptureAsync(Server, cancellationToken).ConfigureAwait(false);
        Session value = await owner.GetSessionAsync(Id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new OwnedSessionScope(value);
    }
}

public sealed partial class Window
{
    /// <summary>Accepts destruction responsibility for this window and its panes.</summary>
    /// <param name="cancellationToken">Cancels identity acceptance before ownership transfers.</param>
    /// <returns>An owner retaining the window ID through rename, linking and moves.</returns>
    /// <remarks>Disposal kills the window and all its links; it does not merely unlink one session.</remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<OwnedWindowScope> AdoptAsync(CancellationToken cancellationToken = default)
    {
        Server owner = await TmuxOwnershipIdentity.CaptureAsync(Server, cancellationToken).ConfigureAwait(false);
        Window value = await owner.GetWindowAsync(Id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new OwnedWindowScope(value);
    }
}

public sealed partial class Pane
{
    /// <summary>Accepts destruction responsibility for this pane.</summary>
    /// <param name="cancellationToken">Cancels identity acceptance before ownership transfers.</param>
    /// <returns>An owner retaining the pane ID through moves.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<OwnedPaneScope> AdoptAsync(CancellationToken cancellationToken = default)
    {
        Server owner = await TmuxOwnershipIdentity.CaptureAsync(Server, cancellationToken).ConfigureAwait(false);
        Pane value = await owner.GetPaneAsync(Id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new OwnedPaneScope(value);
    }

    /// <summary>Splits a pane and takes ownership of the created pane.</summary>
    /// <param name="request">The split options.</param>
    /// <param name="cancellationToken">Cancels acquisition before publication.</param>
    /// <returns>The owner of the new pane.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<OwnedPaneScope> SplitOwnedAsync(SplitPaneRequest? request = null, CancellationToken cancellationToken = default) =>
        new(await SplitAsync(request, cancellationToken).ConfigureAwait(false));
}
