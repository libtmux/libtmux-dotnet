namespace LibTmux;

/// <summary>Reports a command or feature that is unsafe or unavailable on the running tmux release.</summary>
/// <remarks>
/// <para>
/// Every refusal for a release reason raises this type or a subclass, so one
/// catch block covers a release that lacks a feature, a release that crashes
/// on a form, and a release that rejects an input other releases accept.
/// <see cref="TmuxVersionTooLowException" /> is the subclass for a plain
/// release floor. A typed call that leaves a flag out on an older release,
/// such as <c>Server.RefreshClientAsync</c> dropping <c>-l</c>, skips the
/// flag and raises nothing.
/// </para>
/// <para>
/// The library decides a refusal before anything is sent, so the exceptions it
/// raises carry <see cref="TmuxDispatchState.NotDispatched" /> in
/// <see cref="LibTmuxException.Dispatch" />, and sending the command again on
/// the same release repeats the refusal. An input that is invalid on every
/// release is not this type; it is an <see cref="ArgumentException" />.
/// </para>
/// </remarks>
public class TmuxUnsupportedOnReleaseException : LibTmuxException
{
    /// <summary>Initializes a release refusal.</summary>
    /// <param name="message">What was refused and why.</param>
    /// <param name="actualVersion">The running tmux release.</param>
    /// <param name="safeReleases">The releases where the command is safe, in words.</param>
    /// <param name="capabilityId">The id in <c>capabilities.json</c> that covers the refusal, if any.</param>
    /// <param name="command">The refused tmux command, if any.</param>
    /// <param name="innerException">The underlying failure, when any.</param>
    public TmuxUnsupportedOnReleaseException(
        string message,
        TmuxVersion actualVersion,
        string safeReleases,
        string? capabilityId = null,
        string? command = null,
        Exception? innerException = null)
        : this(message, actualVersion, safeReleases, capabilityId, command, TmuxDispatchState.Unknown, innerException)
    {
    }

    internal TmuxUnsupportedOnReleaseException(
        string message,
        TmuxVersion actualVersion,
        string safeReleases,
        string? capabilityId,
        string? command,
        TmuxDispatchState dispatch,
        Exception? innerException = null)
        : base(message, dispatch, innerException)
    {
        ActualVersion = actualVersion;
        SafeReleases = safeReleases;
        CapabilityId = capabilityId;
        Command = command;
    }

    /// <summary>Gets the running tmux release.</summary>
    public TmuxVersion ActualVersion { get; }

    /// <summary>Gets the releases where the command is safe, such as <c>3.4 or newer</c>.</summary>
    public string SafeReleases { get; }

    /// <summary>Gets the capability or workaround id in <c>capabilities.json</c>, or null when none covers the refusal.</summary>
    public string? CapabilityId { get; }

    /// <summary>Gets the refused tmux command, or null when the refusal is not about one command.</summary>
    public string? Command { get; }
}
