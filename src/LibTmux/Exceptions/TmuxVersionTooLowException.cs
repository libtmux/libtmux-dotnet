namespace LibTmux;

/// <summary>Reports an unsupported tmux version.</summary>
/// <remarks>
/// A report that names no release the library can rank, such as <c>master</c>,
/// is refused the same way with an <see cref="ActualVersion" /> that is not
/// valid: the build may be newer than the minimum, but nothing can say so.
/// </remarks>
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
