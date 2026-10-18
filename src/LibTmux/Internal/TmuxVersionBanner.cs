namespace LibTmux.Internal;

internal readonly record struct TmuxVersionBanner(
    TmuxImplementation Implementation,
    string RawVersion,
    string Version,
    string? ImplementationLine);

internal static class TmuxVersionBannerParser
{
    // tmux 3.3a prints the executable's own basename in place of "tmux", so a
    // build installed as tmux-3.3a reports "tmux-3.3a 3.3a". Later releases
    // print "tmux" whatever the file is called.
    internal static bool TryParse(
        IReadOnlyList<string> lines,
        out TmuxVersionBanner banner,
        string? executableName = null)
    {
        banner = default;
        if (lines.Count is < 1 or > 2)
        {
            return false;
        }

        string first = lines[0];
        if (!TryReadVersion(first, executableName, out TmuxVersion version))
        {
            return false;
        }

        // The raw form is the one every reader of a handle's version expects,
        // whatever name the executable reported itself under.
        string raw = $"tmux {version.Raw}";
        if (lines.Count == 1)
        {
            banner = new TmuxVersionBanner(
                TmuxImplementation.Tmux,
                raw,
                version.Raw,
                ImplementationLine: null);
            return true;
        }

        string prefix = $"psmux {version.Raw}";
        string second = lines[1];
        if (!second.StartsWith(prefix, StringComparison.Ordinal)
            || (second.Length != prefix.Length
                && !(second.Length > prefix.Length + 3
                    && second[prefix.Length] == ' '
                    && second[prefix.Length + 1] == '('
                    && second[^1] == ')')))
        {
            return false;
        }

        banner = new TmuxVersionBanner(
            TmuxImplementation.Psmux,
            raw,
            version.Raw,
            second);
        return true;
    }

    /// <summary>Reads the version from one <c>-V</c> line.</summary>
    internal static bool TryReadVersion(
        string line,
        string? executableName,
        out TmuxVersion version)
    {
        version = default;
        if (line.StartsWith("tmux ", StringComparison.Ordinal))
        {
            return TmuxVersion.TryParse(line[5..], out version);
        }

        if (!string.IsNullOrEmpty(executableName)
            && line.StartsWith(executableName + " ", StringComparison.Ordinal))
        {
            return TmuxVersion.TryParse(line[(executableName.Length + 1)..], out version);
        }

        return false;
    }
}
