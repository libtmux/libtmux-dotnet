using System.Runtime.ExceptionServices;

namespace LibTmux.Testing;

internal static class TemporaryScopeCleanup
{
    private const string SecondaryFailureDataKey = "LibTmux.Testing.SecondaryCleanupFailure";

    internal static async ValueTask DisposeAsync(
        IAsyncDisposable value,
        IAsyncDisposable? parent)
    {
        Exception? failure = null;
        try
        {
            await value.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }

        if (parent is not null)
        {
            try
            {
                await parent.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (failure is null)
                {
                    failure = error;
                }
                else
                {
                    failure.Data[SecondaryFailureDataKey] = error;
                }
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    internal static async Task DisposeAfterFailureAsync(
        IAsyncDisposable value,
        Exception primaryFailure)
    {
        try
        {
            await value.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            primaryFailure.Data[SecondaryFailureDataKey] = cleanupFailure;
        }
    }
}
