namespace LibTmux;

// Provides captured tmux version metadata.
public sealed partial class Server
{
    /// <summary>Gets the captured tmux version.</summary>
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
