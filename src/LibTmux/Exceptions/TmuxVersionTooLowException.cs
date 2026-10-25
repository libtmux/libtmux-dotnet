namespace LibTmux;

/// <summary>Reports an unsupported tmux version.</summary>
/// <remarks>
/// A report that names no release the library can rank, such as <c>master</c>,
/// is refused the same way with an <see cref="TmuxUnsupportedOnReleaseException.ActualVersion" />
/// that is not valid: the build may be newer than the minimum, but nothing can say so.
/// Catch <see cref="TmuxUnsupportedOnReleaseException" /> to cover refusals that
/// are not a plain floor as well.
/// </remarks>
public sealed class TmuxVersionTooLowException : TmuxUnsupportedOnReleaseException
{
    /// <summary>Initializes an unsupported-version exception.</summary>
    public TmuxVersionTooLowException(
        string message,
        TmuxVersion requiredVersion,
        TmuxVersion actualVersion,
        Exception? innerException = null)
        : base(message, actualVersion, SafeFrom(requiredVersion), null, null, innerException)
        => RequiredVersion = requiredVersion;

    internal TmuxVersionTooLowException(
        string message,
        TmuxVersion requiredVersion,
        TmuxVersion actualVersion,
        TmuxDispatchState dispatch,
        string? capabilityId = null,
        string? command = null,
        string? safeReleases = null)
        : base(message, actualVersion, safeReleases ?? SafeFrom(requiredVersion), capabilityId, command, dispatch)
        => RequiredVersion = requiredVersion;

    /// <summary>Gets the required tmux version.</summary>
    public TmuxVersion RequiredVersion { get; }

    private static string SafeFrom(TmuxVersion required) => $"{required} or newer";
}
