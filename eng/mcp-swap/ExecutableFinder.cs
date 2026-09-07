namespace LibTmux.McpSwap;

internal static class ExecutableFinder
{
    internal static string? Find(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return File.Exists(command) ? Path.GetFullPath(command) : null;
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
            {
                return NativeFileSystem.RealPath(candidate);
            }
        }

        return null;
    }
}
