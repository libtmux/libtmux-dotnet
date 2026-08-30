using System.Diagnostics;

namespace LibTmux.Internal;

internal static class PsmuxProcessEnvironment
{
    /// <summary>Reports whether a launch has to carry the data directory into WSL.</summary>
    /// <remarks>
    /// A Windows psmux executable started from Linux reads its data directory
    /// through the interop layer, which does not forward the variable on its own.
    /// </remarks>
    internal static bool ForwardsDataDirectoryThroughWsl(ServerConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.PsmuxPreview is not null
            && !OperatingSystem.IsWindows()
            && string.Equals(
                Path.GetExtension(options.TmuxBinaryPath),
                ".exe",
                StringComparison.OrdinalIgnoreCase);
    }

    internal static void Apply(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?>? childEnvironment,
        bool forwardDataDirectoryThroughWsl)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        Remove(startInfo, "TMUX");
        Remove(startInfo, "TMUX_PANE");
        string[] inheritedPsmuxVariables =
        [
            .. startInfo.Environment.Keys.Where(IsPsmuxVariable),
        ];
        foreach (string variable in inheritedPsmuxVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        if (childEnvironment is null)
        {
            return;
        }

        foreach ((string key, string? value) in childEnvironment)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (value is null)
            {
                startInfo.Environment.Remove(key);
            }
            else
            {
                startInfo.Environment[key] = value;
            }
        }

        if (forwardDataDirectoryThroughWsl)
        {
            ForwardDataDirectoryThroughWsl(startInfo);
        }
    }

    private static void ForwardDataDirectoryThroughWsl(ProcessStartInfo startInfo)
    {
        if (!startInfo.Environment.TryGetValue("PSMUX_DATA_DIR", out string? dataDirectory)
            || string.IsNullOrEmpty(dataDirectory))
        {
            throw new InvalidOperationException(
                "The psmux preview requires a child PSMUX_DATA_DIR value.");
        }

        var entries = new List<string>();
        foreach ((string key, string? value) in startInfo.Environment)
        {
            if (!string.Equals(key, "WSLENV", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(value))
            {
                continue;
            }

            foreach (string entry in value.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                int modifier = entry.IndexOf('/');
                string variable = modifier < 0 ? entry : entry[..modifier];
                if (!IsRoutingVariable(variable))
                {
                    entries.Add(entry);
                }
            }
        }

        Remove(startInfo, "WSLENV");
        entries.Add("PSMUX_DATA_DIR/w");
        startInfo.Environment["WSLENV"] = string.Join(':', entries);
    }

    private static bool IsRoutingVariable(string name) =>
        string.Equals(name, "TMUX", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "TMUX_PANE", StringComparison.OrdinalIgnoreCase)
        || IsPsmuxVariable(name);

    private static bool IsPsmuxVariable(string name) =>
        name.StartsWith("PSMUX_", StringComparison.OrdinalIgnoreCase);

    private static void Remove(ProcessStartInfo startInfo, string name)
    {
        string[] matches =
        [
            .. startInfo.Environment.Keys.Where(
                key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)),
        ];
        foreach (string key in matches)
        {
            startInfo.Environment.Remove(key);
        }
    }
}
