namespace LibTmux;

// Provides captured tmux version metadata.
public sealed partial class Server
{
    /// <summary>Gets the daemon version read by inspection, or null when it was not acquired.</summary>
    public TmuxVersion? DaemonVersion { get; }

    /// <summary>Gets the verified tmux client executable version.</summary>
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
