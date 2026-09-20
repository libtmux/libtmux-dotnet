namespace LibTmux;

/// <summary>Thrown when tmux refuses an option name or value.</summary>
/// <remarks>
/// tmux distinguishes unknown, invalid, and ambiguous options, and which one it
/// says depends on its version rather than on anything a caller did. They are
/// one failure here, carrying tmux's own wording and the name that caused it.
/// </remarks>
public sealed class TmuxOptionException : LibTmuxException
{
    /// <summary>Initializes the exception for one rejected option.</summary>
    /// <param name="message">What tmux reported.</param>
    /// <param name="optionName">The option tmux was asked about.</param>
    /// <param name="innerException">The underlying failure, when any.</param>
    public TmuxOptionException(
        string message,
        string optionName,
        Exception? innerException = null)
        : this(message, optionName, TmuxDispatchState.Unknown, innerException)
    {
    }

    internal TmuxOptionException(
        string message,
        string optionName,
        TmuxDispatchState dispatch,
        Exception? innerException = null)
        : base(message, dispatch, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionName);
        OptionName = optionName;
    }

    /// <summary>Gets the option tmux was asked about.</summary>
    public string OptionName { get; }
}
