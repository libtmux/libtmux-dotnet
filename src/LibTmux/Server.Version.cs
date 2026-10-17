using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Provides captured tmux version metadata.
public sealed partial class Server
{
    /// <summary>Gets the daemon version read by inspection, or null when it was not acquired.</summary>
    public TmuxVersion? DaemonVersion { get; }

    /// <summary>Reads the version this handle was checked on, asking the daemon when it has none.</summary>
    [UnsupportedOSPlatform("windows")]
    internal async Task<TmuxVersion> ServerVersionAsync(CancellationToken cancellationToken)
    {
        if (Version is TmuxVersion known)
        {
            return known;
        }

        Server found = await RediscoverCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
        return found.Version
            ?? throw new TmuxProtocolException("The server reported no version.", TmuxDispatchState.NotDispatched);
    }

    /// <summary>Refuses work that stages a named buffer on a release that cannot clean it up exactly.</summary>
    /// <param name="operation">What needs the buffer, for the message.</param>
    /// <param name="cancellationToken">Cancels version discovery.</param>
    /// <exception cref="TmuxVersionTooLowException">tmux is older than 3.4.</exception>
    [UnsupportedOSPlatform("windows")]
    internal async Task RequireBufferStagingAsync(string operation, CancellationToken cancellationToken) =>
        TmuxFeatureGuard.RequireNamedBuffers(
            await ServerVersionAsync(cancellationToken).ConfigureAwait(false),
            operation);

    /// <summary>Gets the tmux version this handle was checked on.</summary>
    /// <remarks>
    /// The connected daemon's version, or the executable's when no daemon was
    /// running and the executable would start the next one.
    /// </remarks>
    public TmuxVersion? Version
    {
        get
        {
            const string prefix = "tmux ";
            if (RawVersion is null
                || !RawVersion.StartsWith(prefix, StringComparison.Ordinal)
                || !TmuxVersion.TryParse(RawVersion[prefix.Length..], out TmuxVersion version))
            {
                return null;
            }

            return version;
        }
    }
}
