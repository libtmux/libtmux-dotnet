namespace LibTmux;

/// <summary>Thrown when a pane operation is refused before tmux sees it.</summary>
/// <remarks>
/// Reserved for refusals that are about the pane rather than the shape of the
/// request: an argument the caller got wrong stays an
/// <see cref="ArgumentException" />, and a command tmux rejected stays a
/// <see cref="TmuxCommandException" />.
/// </remarks>
public sealed class TmuxPaneException : LibTmuxException
{
    /// <summary>Initializes the exception for one pane.</summary>
    /// <param name="message">What was refused.</param>
    /// <param name="paneId">The pane the request named.</param>
    /// <param name="innerException">The underlying failure, when any.</param>
    public TmuxPaneException(string message, PaneId paneId, Exception? innerException = null)
        : base(message, innerException) => PaneId = paneId;

    // A refusal decided before anything is sent says so; a pane read can also
    // fail after keys went out, so the public constructor stays Unknown.
    internal TmuxPaneException(string message, PaneId paneId, TmuxDispatchState dispatch)
        : base(message, dispatch) => PaneId = paneId;

    /// <summary>Gets the pane the request named.</summary>
    public PaneId PaneId { get; }
}
