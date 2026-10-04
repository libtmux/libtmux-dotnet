using LibTmux.Internal;

namespace LibTmux;

/// <summary>Reports a command-policy failure.</summary>
public sealed class TmuxCommandException : LibTmuxException
{
    // A result exists only because a tmux client ran, so the dispatch state
    // comes from the result, not a constructor parameter: Dispatched, unless
    // the client reports that no server is listening, which no server heard.

    /// <summary>Initializes a command exception.</summary>
    public TmuxCommandException(
        string message,
        TmuxCommandResult result,
        Exception? innerException = null)
        : base(
            message,
            TmuxCommandFailure.DispatchOf(result ?? throw new ArgumentNullException(nameof(result))),
            innerException)
        => Result = result;

    /// <summary>Gets the inspectable command result.</summary>
    public TmuxCommandResult Result { get; }
}
