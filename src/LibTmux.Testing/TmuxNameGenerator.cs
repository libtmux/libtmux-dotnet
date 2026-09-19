using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux.Testing;

/// <summary>Makes names no other test is using.</summary>
/// <remarks>
/// Two tests that pick the same session name find each other's sessions, and
/// the failure lands in whichever ran second. A name carries enough randomness
/// to be unique without a round trip, and the asking variants also check with
/// the server for the case where something outside the test took the name.
/// </remarks>
public sealed class TmuxNameGenerator
{
    private const int Attempts = 32;
    private readonly string _prefix;
    private int _counter;

    /// <summary>Initializes a generator.</summary>
    /// <param name="prefix">What every name it makes starts with.</param>
    public TmuxNameGenerator(string prefix = "lt")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        SessionName.Validate(prefix);
        _prefix = prefix;
    }

    /// <summary>Makes a session name.</summary>
    /// <returns>The name.</returns>
    public string CreateSessionName() => Create("s");

    /// <summary>Makes a window name.</summary>
    /// <returns>The name.</returns>
    public string CreateWindowName() => Create("w");

    /// <summary>Makes a session name the server does not already hold.</summary>
    /// <param name="server">The server to ask.</param>
    /// <param name="prefix">What the name starts with, or null for this generator's.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The name.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<string> CreateAvailableSessionNameAsync(
        Server server,
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            string candidate = Create("s", prefix);
            if (!await server.HasSessionAsync(candidate, true, cancellationToken)
                .ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"No unused session name starting with '{prefix ?? _prefix}' was found.");
    }

    /// <summary>Makes a window name the session does not already hold.</summary>
    /// <param name="session">The session to ask.</param>
    /// <param name="prefix">What the name starts with, or null for this generator's.</param>
    /// <param name="cancellationToken">Cancels the tmux commands.</param>
    /// <returns>The name.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<string> CreateAvailableWindowNameAsync(
        Session session,
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            string candidate = Create("w", prefix);
            IReadOnlyList<Window> windows = await session.GetWindowsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!windows.Any(window =>
                string.Equals(window.Name, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"No unused window name starting with '{prefix ?? _prefix}' was found.");
    }

    private string Create(string kind, string? prefix = null)
    {
        // The counter separates names made in the same tick, and the random
        // part separates one test run from another.
        int ordinal = Interlocked.Increment(ref _counter);
        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix ?? _prefix}{kind}{ordinal}-{Guid.NewGuid():N}");
        return name[..Math.Min(24, name.Length)];
    }
}
