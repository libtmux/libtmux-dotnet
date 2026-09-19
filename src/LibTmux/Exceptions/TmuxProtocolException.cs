namespace LibTmux;

/// <summary>Reports an answer from tmux this library could not read.</summary>
/// <remarks>
/// Distinct from <see cref="TmuxCommandException" />, which means tmux reported
/// a failure it understood. This means tmux reported success, or sent an event,
/// and the bytes did not decode into anything the library can use: a row
/// missing a field it must have, a framed value that never ended, a
/// control-mode block guard out of place. A caller cannot correct one of these
/// by changing its arguments, so the useful response is to report it rather
/// than retry it — and <see cref="LibTmuxException.Dispatch" /> says whether a
/// retry would repeat a side effect anyway.
/// </remarks>
public sealed class TmuxProtocolException : LibTmuxException
{
    /// <summary>Initializes the exception for an unreadable answer.</summary>
    /// <param name="message">What could not be read.</param>
    /// <param name="dispatch">Whether tmux had already acted.</param>
    /// <param name="innerException">The failure underneath, when there is one.</param>
    public TmuxProtocolException(
        string message,
        TmuxDispatchState dispatch,
        Exception? innerException = null)
        : base(message, dispatch, innerException) => Payload = string.Empty;

    /// <summary>Initializes the exception naming what tmux sent.</summary>
    /// <param name="message">What could not be read.</param>
    /// <param name="payload">The text that could not be read.</param>
    /// <param name="dispatch">Whether tmux had already acted.</param>
    /// <param name="innerException">The failure underneath, when there is one.</param>
    /// <remarks>
    /// A refusal that does not name what it refused leaves the caller to
    /// reproduce the command to see it, which is the failure's own work.
    /// </remarks>
    public TmuxProtocolException(
        string message,
        string payload,
        TmuxDispatchState dispatch,
        Exception? innerException = null)
        : base(message, dispatch, innerException)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Payload = payload;
    }

    /// <summary>Gets what tmux sent that could not be read.</summary>
    /// <remarks>Empty when the failure captured no single value.</remarks>
    public string Payload { get; }
}
