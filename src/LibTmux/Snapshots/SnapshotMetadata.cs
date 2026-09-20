namespace LibTmux;

/// <summary>Describes the interval and scope of one successful hierarchy acquisition.</summary>
/// <remarks>
/// Separate tmux reads do not form a transaction. The recorded interval includes
/// discovery, initialization, reads, validation, and graph assembly.
/// </remarks>
public sealed class SnapshotMetadata
{
    internal SnapshotMetadata(
        SnapshotDepth depth,
        ServerGeneration generation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        TimeSpan elapsed)
    {
        Depth = depth;
        Generation = generation;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        Elapsed = elapsed;
    }

    /// <summary>Gets the requested depth that was successfully acquired.</summary>
    public SnapshotDepth Depth { get; }

    /// <summary>Gets the daemon generation that answered the acquisition.</summary>
    public ServerGeneration Generation { get; }

    /// <summary>Gets the UTC clock reading before acquisition began.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Gets the UTC clock reading after graph assembly completed.</summary>
    /// <remarks>
    /// Clock adjustment can place this before <see cref="StartedAtUtc"/>.
    /// Use <see cref="Elapsed"/> for the duration.
    /// </remarks>
    public DateTimeOffset CompletedAtUtc { get; }

    /// <summary>Gets the acquisition duration measured with a monotonic clock.</summary>
    public TimeSpan Elapsed { get; }
}
