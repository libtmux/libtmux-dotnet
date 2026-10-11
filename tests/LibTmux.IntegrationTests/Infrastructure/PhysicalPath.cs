namespace LibTmux.IntegrationTests.Infrastructure;

/// <summary>Resolves a path the way the kernel and tmux report it.</summary>
internal static class PhysicalPath
{
    /// <summary>Follows every symlink in <paramref name="path"/>, one segment at a time.</summary>
    /// <remarks>
    /// tmux and a shell's <c>$PWD</c> report the physical directory, so on
    /// macOS <c>/tmp/x</c> comes back as <c>/private/tmp/x</c>. Resolve the
    /// expected side before comparing it with what they report.
    /// </remarks>
    internal static string Resolve(string path)
    {
        string current = "/";
        foreach (string segment in Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                current = Resolve(target.FullName);
        }

        return current;
    }
}
