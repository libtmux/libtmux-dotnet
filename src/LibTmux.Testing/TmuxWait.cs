namespace LibTmux.Testing;

/// <summary>Waits for tmux to reach a state instead of sleeping.</summary>
/// <remarks>
/// tmux answers a command as soon as it has accepted it, not once the effect
/// has landed, so a test that acts and then reads immediately is racing the
/// server. A fixed delay only hides that on an idle machine; waiting for the
/// state itself is what makes the test say what it means.
/// </remarks>
public static class TmuxWait
{
    /// <summary>Waits until a probe reports the state was reached.</summary>
    /// <param name="probe">Answers whether the state has been reached.</param>
    /// <param name="timeout">How long to keep asking.</param>
    /// <param name="interval">How long to wait between askings.</param>
    /// <param name="throwOnTimeout">Whether running out is a failure.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>True when the state was reached.</returns>
    /// <exception cref="TmuxWaitTimeoutException">
    /// The state was not reached and running out was to be a failure.
    /// </exception>
    public static async Task<bool> UntilAsync(
        Func<CancellationToken, Task<bool>> probe,
        TimeSpan timeout,
        TimeSpan interval,
        bool throwOnTimeout = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ValidateBounds(timeout, interval);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await probe(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            // The deadline is checked after asking so that a probe which is
            // already true never waits, whatever the timeout.
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return throwOnTimeout
                    ? throw new TmuxWaitTimeoutException(
                        $"tmux did not reach the expected state within {timeout}.",
                        timeout)
                    : false;
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until a reading satisfies a predicate, and answers it.</summary>
    /// <typeparam name="T">What is being read.</typeparam>
    /// <param name="probe">Reads the current value.</param>
    /// <param name="predicate">Answers whether a reading is the wanted one.</param>
    /// <param name="timeout">How long to keep reading.</param>
    /// <param name="interval">How long to wait between readings.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The first reading the predicate accepted.</returns>
    /// <exception cref="TmuxWaitTimeoutException">No reading was accepted in time.</exception>
    public static async Task<T> UntilAsync<T>(
        Func<CancellationToken, Task<T>> probe,
        Func<T, bool> predicate,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(predicate);
        ValidateBounds(timeout, interval);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        T reading;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reading = await probe(cancellationToken).ConfigureAwait(false);
            if (predicate(reading))
            {
                return reading;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TmuxWaitTimeoutException(
                    $"tmux did not reach the expected state within {timeout}.",
                    timeout);
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateBounds(TimeSpan timeout, TimeSpan interval)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "A wait needs time to wait in.");
        }

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                interval,
                "Asking tmux without pausing would spin.");
        }
    }
}
