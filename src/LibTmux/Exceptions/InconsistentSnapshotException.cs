namespace LibTmux;

/// <summary>Reports contradictory topology in completed snapshot reads.</summary>
public sealed class InconsistentSnapshotException : LibTmuxException
{
    /// <summary>Initializes a failure for one unsuccessful hierarchy acquisition.</summary>
    /// <param name="message">The observed parent, placement, or count contradiction.</param>
    /// <param name="requestedDepth">The depth requested by the caller.</param>
    /// <param name="generation">The daemon generation that answered the reads.</param>
    public InconsistentSnapshotException(
        string message,
        SnapshotDepth requestedDepth,
        ServerGeneration generation)
        : base(message, TmuxDispatchState.Dispatched)
    {
        RequestedDepth = requestedDepth;
        Generation = generation;
    }

    /// <summary>Gets the depth requested by the unsuccessful acquisition.</summary>
    public SnapshotDepth RequestedDepth { get; }

    /// <summary>Gets the daemon generation that answered the completed reads.</summary>
    public ServerGeneration Generation { get; }
}
