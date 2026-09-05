namespace LibTmux;

/// <summary>Thrown when a window operation is refused before tmux sees it.</summary>
/// <remarks>
/// Some window requests cannot safely be handed to tmux to reject. tmux 3.3a
/// crashes its whole server on an unrecognised layout name, taking every
/// session on the socket with it, so a bad layout is refused here instead.
/// </remarks>
public sealed class TmuxWindowException : LibTmuxException
{
    /// <summary>Initializes the exception for one window.</summary>
    /// <param name="message">What was refused.</param>
    /// <param name="windowId">The window the request named.</param>
    /// <param name="innerException">The underlying failure, when any.</param>
    public TmuxWindowException(
        string message,
        WindowId windowId,
        Exception? innerException = null)
        : base(message, innerException) => WindowId = windowId;

    /// <summary>Initializes the exception for one window, stating whether tmux ran.</summary>
    /// <param name="message">What was refused.</param>
    /// <param name="windowId">The window the request named.</param>
    /// <param name="dispatch">Whether the command reached tmux.</param>
    /// <param name="innerException">The underlying failure, when any.</param>
    /// <remarks>
    /// A refusal decided before anything is sent can say so, which is what
    /// stops a caller being told a failed validation may have changed tmux.
    /// </remarks>
    public TmuxWindowException(
        string message,
        WindowId windowId,
        TmuxDispatchState dispatch,
        Exception? innerException = null)
        : base(message, dispatch, innerException) => WindowId = windowId;

    /// <summary>Gets the window the request named.</summary>
    public WindowId WindowId { get; }
}
