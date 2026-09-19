namespace LibTmux.Testing;

/// <summary>How a test's tmux is reached and how long it is waited on.</summary>
/// <remarks>
/// Left alone, a test gets a socket nothing else can be using. Falling back to
/// the ambient connection would point a throwaway server at the developer's own
/// tmux and kill it on the way out.
/// </remarks>
public sealed record TmuxTestOptions
{
    /// <summary>Initializes test options.</summary>
    /// <param name="connectionOptions">How to reach tmux, or null for a socket of its own.</param>
    /// <param name="timeout">How long a wait keeps asking.</param>
    /// <param name="pollInterval">How long a wait pauses between askings.</param>
    /// <param name="sessionNamePrefix">What generated names start with.</param>
    public TmuxTestOptions(
        ServerConnectionOptions? connectionOptions = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string sessionNamePrefix = "lt")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionNamePrefix);
        if (timeout is TimeSpan waiting && waiting <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "A wait needs time to wait in.");
        }

        if (pollInterval is TimeSpan pause && pause <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval),
                pollInterval,
                "Asking tmux without pausing would spin.");
        }

        ConnectionOptions = connectionOptions
            ?? new ServerConnectionOptions(socketName: $"libtmux-{Guid.NewGuid():N}");
        Timeout = timeout ?? TimeSpan.FromSeconds(10);
        PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(20);
        SessionNamePrefix = sessionNamePrefix;
    }

    /// <summary>Gets options a test can use without choosing anything.</summary>
    public static TmuxTestOptions Default { get; } = new();

    /// <summary>Gets how to reach tmux.</summary>
    public ServerConnectionOptions ConnectionOptions { get; }

    /// <summary>Gets how long a wait keeps asking.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Gets how long a wait pauses between askings.</summary>
    public TimeSpan PollInterval { get; }

    /// <summary>Gets what generated names start with.</summary>
    public string SessionNamePrefix { get; }
}
