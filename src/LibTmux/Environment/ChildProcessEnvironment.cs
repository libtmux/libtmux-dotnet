using System.Diagnostics;

namespace LibTmux.Internal;

/// <summary>Takes <c>TMUX</c> out of a tmux child process's environment unless the caller names it.</summary>
/// <remarks>
/// A process running inside a pane inherits <c>TMUX</c>, which names that
/// pane's server. A client given a socket with <c>-L</c> or <c>-S</c> is meant
/// to reach that socket alone, so the child does not keep it. A connection that
/// chose no socket names <c>TMUX</c> in the overrides, and tmux then follows the
/// server the process runs in. Isolation happens on the child's environment
/// block so the calling process's own environment is never mutated.
/// </remarks>
internal static class ChildProcessEnvironment
{
    /// <summary>Applies an isolated environment to one child process.</summary>
    /// <param name="startInfo">The child process description.</param>
    /// <param name="overrides">Values to set, or null values to remove.</param>
    internal static void Apply(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?>? overrides)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Environment.Remove(TmuxEnvironmentVariables.ServerVariable);
        if (overrides is null)
        {
            return;
        }

        foreach ((string key, string? value) in overrides)
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
    }
}
