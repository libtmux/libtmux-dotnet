using System.Diagnostics;

namespace LibTmux.Internal;

/// <summary>Isolates a tmux child process from an inherited tmux session.</summary>
/// <remarks>
/// A process running inside a pane inherits <c>TMUX</c>, and a bare tmux client
/// that sees it will refuse to nest or will target the wrong server. Isolation
/// happens on the child's environment block so the calling process's own
/// environment is never mutated.
/// </remarks>
internal static class ChildProcessEnvironment
{
    /// <summary>Applies an isolated environment to one child process.</summary>
    /// <param name="startInfo">The child process description.</param>
    /// <param name="overrides">Values to set, or null values to remove.</param>
    internal static void Apply(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?>? overrides) =>
        PsmuxProcessEnvironment.Apply(startInfo, overrides, forwardDataDirectoryThroughWsl: false);
}
