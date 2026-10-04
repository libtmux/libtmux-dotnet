namespace LibTmux;

/// <summary>Provides the base exception for remote tmux failures.</summary>
/// <remarks>
/// Every failure carries a <see cref="Dispatch"/> state, so the question a
/// caller asks in a catch block — is it safe to run this again? — is answered
/// on the exception rather than inferred from its type.
/// </remarks>
/// <example>
/// <code>
/// catch (LibTmuxException error)
///     when (error.Dispatch == TmuxDispatchState.NotDispatched)
/// {
///     // tmux never saw it, so sending it again repeats nothing.
/// }
/// </code>
/// </example>
public class LibTmuxException : Exception
{
    /// <summary>Initializes a LibTmux exception whose dispatch state is unknown.</summary>
    public LibTmuxException(string message, Exception? innerException = null)
        : this(message, TmuxDispatchState.Unknown, innerException)
    {
    }

    /// <summary>Initializes a LibTmux exception that knows whether tmux ran the command.</summary>
    public LibTmuxException(
        string message,
        TmuxDispatchState dispatch,
        Exception? innerException = null)
        : base(message, innerException)
        => Dispatch = dispatch;

    /// <summary>Gets whether the command reached tmux, and so whether a retry is safe.</summary>
    /// <remarks>
    /// Defaults to <see cref="TmuxDispatchState.Unknown"/>, which is the state
    /// that does not invite an unsafe retry. A failure only claims
    /// <see cref="TmuxDispatchState.NotDispatched"/> where the code can see
    /// that tmux was never started.
    /// </remarks>
    public TmuxDispatchState Dispatch { get; }
}
