using LibTmux.Internal;

namespace LibTmux.Support;

/// <summary>Reads the connected daemon's version and fences commands on it.</summary>
/// <remarks>
/// <para>
/// The probe is <c>run-shell -C</c> with a marker in front of the version.
/// tmux parses the expanded text as a command, rejects the marker as unknown,
/// and names the whole string in its error. Neither <c>run-shell</c> nor that
/// rejection runs an <c>after-*</c> hook or <c>command-error</c>, where
/// <c>display-message</c> would run its own on every release.
/// </para>
/// <para>
/// A dispatched command list opens with a fence that fails when the daemon is
/// not the one whose version was checked. tmux stops a list at its first failure, so
/// a replaced daemon refuses the commands behind the fence before any runs.
/// </para>
/// </remarks>
internal static class Versions
{
    private const string MarkerPrefix = "libtmux-version-";

    /// <summary>Gets the bytes a fence adds to a command list, for argument budgets.</summary>
    internal const int FenceBudget = 160;

    private static readonly string[] AbsentEndpointPhrases =
    [
        "no server running on ",
        "(No such file or directory)",
        "(Connection refused)",
    ];

    /// <summary>Gets the lowest tmux release whose daemon this library supports.</summary>
    internal static TmuxVersion Minimum => LibTmuxInfo.MinimumTmuxVersion;

    /// <summary>Creates a marker no tmux command can be named.</summary>
    internal static string NewMarker() => MarkerPrefix + Guid.NewGuid().ToString("N");

    /// <summary>Builds the request that reports the daemon's version without effects.</summary>
    /// <param name="marker">The marker the answer is found behind.</param>
    /// <returns>A request that never starts a server.</returns>
    internal static TmuxCommandRequest ProbeRequest(string marker) =>
        TmuxCommandRequest.Group(
            preventServerStart: true,
            ["run-shell", "-C", marker + "#{version}"]);

    /// <summary>Reads a probe's answer.</summary>
    /// <param name="result">What the probing client reported.</param>
    /// <param name="marker">The marker the probe carried.</param>
    /// <returns>The version, an absent endpoint, or a failure.</returns>
    internal static ProbeOutcome ReadProbe(TmuxCommandResult result, string marker)
    {
        ArgumentNullException.ThrowIfNull(result);
        string answer = "unknown command: " + marker;
        if (result.ExitCode == 1
            && result.StandardOutput.IsEmpty
            && result.StandardErrorLines is [string line]
            && line.StartsWith(answer, StringComparison.Ordinal))
        {
            string reported = line[answer.Length..];
            return TmuxVersion.TryParse(reported, out TmuxVersion version)
                ? ProbeOutcome.Reported(version, result)
                : ProbeOutcome.Unrecognized(reported, result);
        }

        return NamesAbsentEndpoint(result) ? ProbeOutcome.Absent(result) : ProbeOutcome.Failed(result);
    }

    /// <summary>Reports whether a result says no daemon answers on the endpoint.</summary>
    internal static bool NamesAbsentEndpoint(TmuxCommandResult result) =>
        result.ExitCode != 0
        && result.StandardOutput.IsEmpty
        && result.StandardErrorLines is [string line]
        && (line.StartsWith(AbsentEndpointPhrases[0], StringComparison.Ordinal)
            || (line.StartsWith("error connecting to ", StringComparison.Ordinal)
                && (line.EndsWith(AbsentEndpointPhrases[1], StringComparison.Ordinal)
                    || line.EndsWith(AbsentEndpointPhrases[2], StringComparison.Ordinal))));

    /// <summary>Refuses a version below the supported minimum.</summary>
    /// <param name="version">The version a daemon or executable reported.</param>
    /// <param name="subject">What reported it, for the message.</param>
    /// <exception cref="TmuxVersionTooLowException">The version is below the minimum.</exception>
    internal static void Require(TmuxVersion version, string subject)
    {
        if (version < Minimum)
        {
            throw new TmuxVersionTooLowException(
                $"tmux {Minimum} or newer is required; {subject} reports {version}.",
                Minimum,
                version,
                TmuxDispatchState.NotDispatched);
        }
    }

    /// <summary>Builds the command that fails unless the daemon reports a version.</summary>
    /// <param name="version">The daemon's version.</param>
    /// <param name="marker">The marker the failure is recognised by.</param>
    /// <returns>An <c>if-shell</c> that does nothing on a match.</returns>
    internal static IReadOnlyList<string> Fence(TmuxVersion version, string marker) =>
        ["if-shell", "-F", $"#{{==:#{{version}},{version.Raw}}}", string.Empty, marker];

    /// <summary>Reports whether a result is a fence refusing a changed daemon.</summary>
    internal static bool IsFenceFailure(TmuxCommandResult result, string marker)
    {
        ArgumentNullException.ThrowIfNull(result);
        string failure = "unknown command: " + marker;
        return result.ExitCode != 0
            && result.StandardErrorLines.Any(line => string.Equals(line, failure, StringComparison.Ordinal));
    }

    /// <summary>Builds the request a fenced command list becomes.</summary>
    internal static TmuxCommandRequest Fenced(
        bool preventServerStart,
        IReadOnlyList<string> fence,
        IReadOnlyList<IReadOnlyList<string>> commands) =>
        TmuxCommandRequest.Group(preventServerStart, [fence, .. commands]);

    /// <summary>Rewrites a fenced result as the commands the caller asked for.</summary>
    internal static TmuxCommandResult Unfenced(
        TmuxCommandResult result,
        IReadOnlyList<IReadOnlyList<string>> commands) =>
        TmuxCommandResultProjection.Remap(
            result,
            TmuxCommandRequest.Group([.. commands]).LogicalArguments,
            result.StandardOutput);

    /// <summary>Builds a successful result for work that had nothing to do.</summary>
    internal static TmuxCommandResult Nothing(IReadOnlyList<string> arguments) =>
        new(arguments, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, [], []);

    internal readonly record struct ProbeOutcome(
        ProbeKind Kind,
        TmuxVersion Version,
        string Text,
        TmuxCommandResult Result)
    {
        internal static ProbeOutcome Absent(TmuxCommandResult result) =>
            new(ProbeKind.Absent, default, string.Empty, result);

        internal static ProbeOutcome Failed(TmuxCommandResult result) =>
            new(ProbeKind.Failed, default, string.Empty, result);

        internal static ProbeOutcome Reported(TmuxVersion version, TmuxCommandResult result) =>
            new(ProbeKind.Version, version, version.Raw, result);

        internal static ProbeOutcome Unrecognized(string text, TmuxCommandResult result) =>
            new(ProbeKind.Unrecognized, default, text, result);
    }

    internal enum ProbeKind
    {
        Version,
        Absent,
        Unrecognized,
        Failed,
    }
}
