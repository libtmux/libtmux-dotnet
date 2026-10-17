using System.Globalization;

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

    private const string NoServerRunning = "no server running on ";
    private const string ErrorConnecting = "error connecting to ";
    private const string NoSuchFile = "(No such file or directory)";
    private const string ConnectionRefused = "(Connection refused)";

    // tmux names only the first word it cannot parse, so the fields are joined
    // with commas rather than spaces.
    private const string Report = "#{version},#{pid},#{start_time}";

    // run-shell gained -C in tmux 3.2, so a 3.1 daemon answers the probe with
    // its usage line (cmd-run-shell.c takes "bt:"). A 3.0 or older daemon
    // speaks a protocol the client refuses before it runs anything.
    private const string RunShellUsage = "usage: run-shell [-b] [-t target-pane] shell-command";
    private const string OldProtocol = "server version is too old for client";

    // A client older than 3.2 does not know -N, which the probe passes.
    private const string UnknownNoStartFlag = ": unknown option -- N";

    /// <summary>Tells a refused caller what to do about it.</summary>
    internal const string Remedy =
        "Upgrade tmux and restart the tmux server, or use LibTmux 0.0.0-alpha.21,"
        + " the last release that supports older tmux.";

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
            ["run-shell", "-C", marker + Report]);

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
            string[] fields = reported.Split(',');
            if (fields.Length != 3 || !TmuxVersion.TryParseReported(fields[0], out TmuxVersion version))
            {
                return ProbeOutcome.Unrecognized(reported, result);
            }

            return int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                && long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long start)
                && pid > 0
                && start > 0
                    ? ProbeOutcome.Reported(version, new ServerGeneration(pid, start), result)
                    : ProbeOutcome.Unrecognized(reported, result);
        }

        if (result.ExitCode != 0
            && result.StandardOutput.IsEmpty
            && result.StandardErrorLines is [string only])
        {
            // The newest release that answers this way, since an older daemon
            // cannot name itself.
            if (string.Equals(only, RunShellUsage, StringComparison.Ordinal))
            {
                return ProbeOutcome.DaemonTooOld(TmuxVersion.Parse("3.1c"), result);
            }

            if (string.Equals(only, OldProtocol, StringComparison.Ordinal))
            {
                return ProbeOutcome.DaemonTooOld(TmuxVersion.Parse("3.0a"), result);
            }
        }

        if (result.ExitCode != 0
            && result.StandardOutput.IsEmpty
            && result.StandardErrorLines is [string flag, ..]
            && flag.EndsWith(UnknownNoStartFlag, StringComparison.Ordinal))
        {
            return ProbeOutcome.ClientTooOld(result);
        }

        return NamesAbsentEndpoint(result) ? ProbeOutcome.Absent(result) : ProbeOutcome.Failed(result);
    }

    /// <summary>Reports whether a result says no daemon answers on the endpoint.</summary>
    internal static bool NamesAbsentEndpoint(TmuxCommandResult result) =>
        result.ExitCode != 0
        && result.StandardOutput.IsEmpty
        && result.StandardErrorLines is [string line]
        && NamesNoDaemon(line);

    // tmux says so either as the server it did not find, or as the socket it
    // could not connect to.
    private static bool NamesNoDaemon(string line) =>
        line.StartsWith(NoServerRunning, StringComparison.Ordinal)
        || (line.StartsWith(ErrorConnecting, StringComparison.Ordinal)
            && (line.EndsWith(NoSuchFile, StringComparison.Ordinal)
                || line.EndsWith(ConnectionRefused, StringComparison.Ordinal)));

    /// <summary>Refuses a version below the supported minimum.</summary>
    /// <param name="version">The version a daemon or executable reported.</param>
    /// <param name="subject">What reported it, for the message.</param>
    /// <exception cref="TmuxVersionTooLowException">The version is below the minimum.</exception>
    internal static void Require(TmuxVersion version, string subject)
    {
        if (version < Minimum)
        {
            throw Refusal($"{subject} reports {version}.", version);
        }
    }

    /// <summary>Builds the refusal of a tmux below the minimum, which sent nothing.</summary>
    /// <param name="problem">The sentence naming what is too old.</param>
    /// <param name="actual">The version, or the newest one that fits the evidence.</param>
    /// <returns>The exception, with the remedy.</returns>
    internal static TmuxVersionTooLowException Refusal(string problem, TmuxVersion actual) =>
        new(
            $"tmux {Minimum} or newer is required; {problem} {Remedy}",
            Minimum,
            actual,
            TmuxDispatchState.NotDispatched);

    /// <summary>Builds the refusal of a version report that names no release, which sent nothing.</summary>
    /// <param name="reported">What the executable or daemon reported.</param>
    /// <returns>The exception, whose <c>ActualVersion</c> is not valid.</returns>
    /// <remarks>
    /// A report that names no release is not "too old": saying the server
    /// needs the minimum would blame a build that may be newer.
    /// </remarks>
    internal static TmuxVersionTooLowException Unrecognized(string reported) =>
        new(
            $"tmux reported an unrecognized version '{WithoutProgramName(reported)}', so its support cannot be checked. "
            + $"Use a tmux release, {Minimum} or newer, and restart the tmux server if one is running.",
            Minimum,
            default,
            TmuxDispatchState.NotDispatched);

    // The banner is "tmux master"; the message already says tmux reported it.
    private static string WithoutProgramName(string reported) =>
        reported.StartsWith("tmux ", StringComparison.Ordinal) ? reported["tmux ".Length..] : reported;

    /// <summary>Builds the command that fails unless the daemon reports a version.</summary>
    /// <param name="version">The daemon's version.</param>
    /// <param name="marker">The marker the failure is recognised by.</param>
    /// <returns>An <c>if-shell</c> that does nothing on a match.</returns>
    internal static IReadOnlyList<string> Fence(TmuxVersion version, string marker) =>
        ["if-shell", "-F", $"#{{==:#{{version}},{version.Raw}}}", string.Empty, marker];

    /// <summary>Reports whether a result is a fence refusing a changed daemon, or a daemon too old to reach it.</summary>
    internal static bool IsFenceFailure(TmuxCommandResult result, string marker)
    {
        ArgumentNullException.ThrowIfNull(result);
        string failure = "unknown command: " + marker;

        // A daemon replaced by one older than tmux 3.1 never reaches the
        // fence: the client turns the connection away first.
        return result.ExitCode != 0
            && result.StandardErrorLines.Any(line =>
                string.Equals(line, failure, StringComparison.Ordinal)
                || string.Equals(line, OldProtocol, StringComparison.Ordinal));
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
        ServerGeneration Generation,
        string Text,
        TmuxCommandResult Result)
    {
        internal static ProbeOutcome Absent(TmuxCommandResult result) =>
            new(ProbeKind.Absent, default, default, string.Empty, result);

        internal static ProbeOutcome Failed(TmuxCommandResult result) =>
            new(ProbeKind.Failed, default, default, string.Empty, result);

        internal static ProbeOutcome Reported(
            TmuxVersion version,
            ServerGeneration generation,
            TmuxCommandResult result) =>
            new(ProbeKind.Version, version, generation, version.Raw, result);

        internal static ProbeOutcome Unrecognized(string text, TmuxCommandResult result) =>
            new(ProbeKind.Unrecognized, default, default, text, result);

        internal static ProbeOutcome DaemonTooOld(TmuxVersion newest, TmuxCommandResult result) =>
            new(ProbeKind.DaemonTooOld, newest, default, newest.Raw, result);

        internal static ProbeOutcome ClientTooOld(TmuxCommandResult result) =>
            new(ProbeKind.ClientTooOld, default, default, string.Empty, result);
    }

    internal enum ProbeKind
    {
        Version,
        Absent,
        Unrecognized,

        /// <summary>The daemon is older than tmux 3.2 and cannot run the probe.</summary>
        DaemonTooOld,

        /// <summary>The client is older than tmux 3.2 and rejects the probe's flags.</summary>
        ClientTooOld,
        Failed,
    }
}
