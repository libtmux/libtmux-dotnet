namespace LibTmux;

/// <summary>Reports an unsupported tmux version.</summary>
public sealed class TmuxVersionTooLowException : LibTmuxException
{
    /// <summary>Initializes an unsupported-version exception.</summary>
    public TmuxVersionTooLowException(
        string message,
        TmuxVersion requiredVersion,
        TmuxVersion actualVersion,
        Exception? innerException = null)
        : base(message, innerException)
    {
        RequiredVersion = requiredVersion;
        ActualVersion = actualVersion;
    }

    internal TmuxVersionTooLowException(
        string message,
        TmuxVersion requiredVersion,
        TmuxVersion actualVersion,
        TmuxDispatchState dispatch)
        : base(message, dispatch)
    {
        RequiredVersion = requiredVersion;
        ActualVersion = actualVersion;
    }

    /// <summary>Gets the required tmux version.</summary>
    public TmuxVersion RequiredVersion { get; }

    /// <summary>Gets the actual tmux version.</summary>
    public TmuxVersion ActualVersion { get; }
}
