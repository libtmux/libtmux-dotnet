using System.Runtime.CompilerServices;
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
    private const string OwnersKey = "LibTmux.CleanupOwners";
    private static readonly ConditionalWeakTable<Exception, object> FailureLocks = new();

    /// <summary>Returns the cleanup failure attached to an exception from an owned scope or acquisition.</summary>
    /// <param name="error">The original body or acquisition exception.</param>
    /// <returns>The teardown error, or null when teardown succeeded.</returns>
    public static Exception? CleanupFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (FailureLocks.GetOrCreateValue(error))
        {
            return error.Data[FailureKey] as Exception;
        }
    }

    /// <summary>Returns owners retained when cleanup failed during acquisition or an owned callback.</summary>
    /// <param name="error">The original body or acquisition exception.</param>
    /// <returns>A read-only snapshot of retryable owners, in the order cleanup failures were recorded, or an empty list.</returns>
    /// <remarks>
    /// <para>Call <see cref="IAsyncDisposable.DisposeAsync" /> on each owner to retry cleanup.
    /// Library owners retain the accepted daemon generation and resource ID, even when acquisition failed before returning a handle.
    /// No owner is returned when acquisition did not establish cleanup authority.</para>
    /// <para>Nested scopes retain each failed owner once, with inner scopes first.
    /// Successfully retried owners remain in the snapshot; repeated disposal is harmless for library owners.
    /// The original exception and <see cref="CleanupFailure(Exception)" /> remain unchanged by retries.</para>
    /// </remarks>
    public static IReadOnlyList<IAsyncDisposable> CleanupOwners(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (FailureLocks.GetOrCreateValue(error))
        {
            return error.Data[OwnersKey] as IReadOnlyList<IAsyncDisposable> ?? [];
        }
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
    /// <see cref="CleanupOwners(Exception)" /> retains the owner if both the body and cleanup fail.
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
            await PreserveCleanupAsync(failure, owner).ConfigureAwait(false);
            throw;
        }

        try
        {
            await owner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            RetainOwner(failure, owner);
            throw;
        }
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

    internal static Task PreserveCleanupAsync(Exception failure, IAsyncDisposable owner) =>
        PreserveCleanupAsync(failure, () => owner.DisposeAsync().AsTask(), owner);

    internal static async Task PreserveCleanupAsync(Exception failure, Func<Task> cleanup, IAsyncDisposable? owner = null)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            lock (FailureLocks.GetOrCreateValue(failure))
            {
                failure.Data[FailureKey] = failure.Data[FailureKey] is Exception previous
                    ? new AggregateException(previous, cleanupFailure)
                    : cleanupFailure;
                if (owner is not null)
                {
                    RetainOwner(failure, owner);
                }
            }
        }
    }

    private static void RetainOwner(Exception failure, IAsyncDisposable owner)
    {
        lock (FailureLocks.GetOrCreateValue(failure))
        {
            IReadOnlyList<IAsyncDisposable> owners = CleanupOwners(failure);
            if (!owners.Any(item => ReferenceEquals(item, owner)))
            {
                failure.Data[OwnersKey] = Array.AsReadOnly<IAsyncDisposable>([.. owners, owner]);
            }
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
