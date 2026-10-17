using System.Runtime.Versioning;
using static LibTmux.Support.Versions;

namespace LibTmux.Internal;

/// <summary>Speaks to a real tmux server.</summary>
/// <remarks>
/// <para>
/// A command reaches the daemon only after the daemon's own version has been
/// checked: it is read once with a probe that fires no hook, refused below
/// the supported minimum, and then written into every command list as a fence.
/// The fence is what makes remembering the answer sound. A daemon that was
/// replaced fails the fence before any command behind it runs, so no
/// generation inherits the version check of the one before it.
/// </para>
/// <para>
/// An endpoint with no daemon takes its version from the executable that
/// would start one, since that is the server the command creates. A connection built on a
/// stand-in transport has no daemon to ask, so it holds the executable's
/// report to the same minimum and sends commands unfenced.
/// </para>
/// </remarks>
internal sealed class TmuxDialect : MultiplexerDialect
{
    private readonly TmuxGenerationGuard _generationGuard;
    private readonly bool _processBacked;
    private readonly string _binaryPath;
    private readonly Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>? _probe;
    private Supported? _supported;

    internal TmuxDialect(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> executeVersion,
        Func<string> markerFactory,
        bool processBacked,
        string binaryPath,
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>? probe = null)
        : base(execute, executeVersion)
    {
        _generationGuard = new TmuxGenerationGuard(execute, markerFactory);
        _processBacked = processBacked;
        _binaryPath = binaryPath;
        _probe = probe;
    }

    internal override bool IsPsmux => false;

    internal override string VerifiedRawVersion =>
        Volatile.Read(ref _supported) is { } supported
            ? $"tmux {supported.Version.Raw}"
            : ClientRawVersion;

    private protected override string ExecutableName => Path.GetFileName(_binaryPath);

    internal override async Task<(ServerGeneration Generation, string RawVersion)> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            // The probe reports the generation with the version in one word,
            // where display-message would fire the owner's after hook.
            await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
            ProbeOutcome found = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (found.Kind is ProbeKind.Version)
            {
                Require(found.Version, "the connected daemon");
                Volatile.Write(ref _supported, new Supported(found.Version, FromDaemon: true));
                return (found.Generation, $"tmux {found.Version.Raw}");
            }

            throw found.Kind is ProbeKind.Absent
                ? new TmuxCommandException(
                    "server generation discovery failed: "
                    + string.Join('\n', found.Result.StandardErrorLines),
                    found.Result)
                : Unreadable(found);
        }

        IReadOnlyList<string> identify = ["display-message", "-p", TmuxConnection.GenerationFormat];
        TmuxCommandResult result = await RunSupportedAsync(
                [identify],
                single: true,
                retryWhenAbsent: true,
                (request, token) => Execute(request, token),
                cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "server generation discovery");

        if (result.StandardOutputLines.Count != 1)
        {
            throw new TmuxCommandException(
                "tmux did not report exactly one server generation.",
                result);
        }

        return (TmuxConnection.ParseGeneration(result.StandardOutputLines[0]), VerifiedRawVersion);
    }

    internal override async Task<(ServerGeneration? Generation, TmuxVersion Version)?> ReadIdentityAsync(
        CancellationToken cancellationToken)
    {
        if (_probe is null)
        {
            return null;
        }

        string client = await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        ProbeOutcome found = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        return found.Kind switch
        {
            ProbeKind.Version => (found.Generation, found.Version),
            ProbeKind.Absent => (null, ReadClientVersion(client)),
            _ => throw Unreadable(found),
        };
    }

    internal override async Task<TmuxCommandResult> ExecuteSingleAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments is ["-V"] or ["-h"])
        {
            // Version and help text describe the executable. They authorize
            // nothing, so they neither need a daemon nor check one.
            await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
            return await Execute(TmuxCommandRequest.Single(arguments), cancellationToken)
                .ConfigureAwait(false);
        }

        if (arguments is ["kill-server"] && _probe is not null
            && await IsAbsentAsync(cancellationToken).ConfigureAwait(false))
        {
            // Stopping a server that is not there is the outcome asked for,
            // and asks nothing of an executable that was never going to
            // start one.
            return Nothing(arguments);
        }

        return await RunSupportedAsync(
                [arguments],
                single: true,
                retryWhenAbsent: true,
                (request, token) => Execute(request, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal override Task<TmuxCommandResult> ExecuteGroupAsync(
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken) =>
        RunSupportedAsync(
            commands,
            single: false,
            retryWhenAbsent: true,
            (request, token) => Execute(request, token),
            cancellationToken);

    internal override Task<TmuxCommandResult> ExecuteGuardedAsync(
        ServerGeneration expected,
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken) =>
        RunGuardedAsync(
            commands,
            (fence, token) => _generationGuard.ExecuteAsync(expected, commands, fence, token),
            cancellationToken);

    [UnsupportedOSPlatform("windows")]
    internal Task<TmuxCommandResult> ExecuteAttachmentAsync(
        ServerGeneration expected,
        IReadOnlyList<string> arguments,
        TimeSpan acknowledgementBudget,
        CancellationToken cancellationToken) =>
        RunGuardedAsync(
            [arguments],
            (fence, token) => _generationGuard.ExecuteAttachmentAsync(
                expected,
                arguments,
                acknowledgementBudget,
                token,
                fence),
            cancellationToken);

    private protected override void AcceptBanner(TmuxVersionBanner banner)
    {
        if (banner.Implementation is TmuxImplementation.Psmux)
        {
            throw new NotSupportedException(
                "psmux requires the explicit PsmuxServer query facade.");
        }
    }

    private protected override void AcceptEndpoint()
    {
        // A Windows executable answers the query facade and nothing else, so
        // the refusal belongs before the command rather than after tmux
        // rejects a flag it never had.
        if (_processBacked
            && (OperatingSystem.IsWindows()
                || string.Equals(
                    Path.GetExtension(_binaryPath),
                    ".exe",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new PlatformNotSupportedException(
                "Windows executables require the explicit PsmuxServer query facade.");
        }
    }

    /// <summary>Runs commands behind a fence on the checked version.</summary>
    /// <param name="commands">The commands as the caller asked for them.</param>
    /// <param name="single">Whether the commands are one raw command sent as it came.</param>
    /// <param name="retryWhenAbsent">
    /// Whether an endpoint that changed since its version check is checked again.
    /// </param>
    /// <param name="send">Sends the request that carries the commands.</param>
    /// <param name="cancellationToken">Cancels the version check and the commands.</param>
    private async Task<TmuxCommandResult> RunSupportedAsync(
        IReadOnlyList<IReadOnlyList<string>> commands,
        bool single,
        bool retryWhenAbsent,
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> send,
        CancellationToken cancellationToken)
    {
        bool checkedAgain = false;
        while (true)
        {
            Supported supported = await CheckVersionAsync(cancellationToken).ConfigureAwait(false);
            TmuxFeatureGuard.Check(supported.Version, commands);
            if (_probe is null)
            {
                return await send(
                        single
                            ? TmuxCommandRequest.Single(commands[0])
                            : TmuxCommandRequest.Group([.. commands]),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            string marker = NewMarker();
            TmuxCommandResult result = await send(
                    Fenced(supported.FromDaemon, Fence(supported.Version, marker), commands),
                    cancellationToken)
                .ConfigureAwait(false);
            if (IsFenceFailure(result, marker))
            {
                // Nothing ran behind the failed fence. A replacement that is
                // supported is checked afresh and the commands are sent
                // again; one that is not is refused.
                ProbeOutcome now = await ReplacementAsync(supported, cancellationToken)
                    .ConfigureAwait(false);
                if (now.Kind is ProbeKind.Version && !checkedAgain)
                {
                    checkedAgain = true;
                    continue;
                }

                throw Changed(supported, now, commands);
            }

            if (supported.FromDaemon
                && !checkedAgain
                && TmuxCommandFailure.NamesMissingServer(result))
            {
                // The daemon went away after its version was checked, and the request
                // could not start another, so nothing ran. Check whatever
                // answers now, or the executable that would start the next.
                Forget(supported);
                checkedAgain = true;
                continue;
            }

            return Unfenced(result, commands);
        }
    }

    /// <summary>Runs commands the generation guard already fences by daemon identity.</summary>
    private async Task<TmuxCommandResult> RunGuardedAsync(
        IReadOnlyList<IReadOnlyList<string>> commands,
        Func<IReadOnlyList<string>?, CancellationToken, Task<TmuxCommandResult>> send,
        CancellationToken cancellationToken)
    {
        Supported supported = await CheckVersionAsync(cancellationToken).ConfigureAwait(false);
        TmuxFeatureGuard.Check(supported.Version, commands);
        if (_probe is null)
        {
            return await send(null, cancellationToken).ConfigureAwait(false);
        }

        string marker = NewMarker();
        TmuxCommandResult result = await send(Fence(supported.Version, marker), cancellationToken)
            .ConfigureAwait(false);
        if (IsFenceFailure(result, marker))
        {
            throw Changed(
                supported,
                await ReplacementAsync(supported, cancellationToken).ConfigureAwait(false),
                commands);
        }

        return result;
    }

    private async Task<Supported> CheckVersionAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _supported) is { } known)
        {
            return known;
        }

        string client = await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        Supported supported;
        if (_probe is null)
        {
            TmuxVersion reported = ReadClientVersion(client);
            Require(reported, "the executable");
            supported = new Supported(reported, FromDaemon: false);
        }
        else
        {
            ProbeOutcome outcome = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            switch (outcome.Kind)
            {
                case ProbeKind.Version:
                    Require(outcome.Version, "the connected daemon");
                    supported = new Supported(outcome.Version, FromDaemon: true);
                    break;
                case ProbeKind.Absent:
                    // No daemon: the executable that creates the next one is
                    // the server the commands will meet.
                    TmuxVersion executable = ReadClientVersion(client);
                    Require(executable, "the executable");
                    supported = new Supported(executable, FromDaemon: false);
                    break;
                default:
                    throw Unreadable(outcome);
            }
        }

        Volatile.Write(ref _supported, supported);
        return supported;
    }

    // Neither answer says the user's command was sent: the probe runs before it.
    private static LibTmuxException Unreadable(ProbeOutcome outcome)
    {
        if (outcome.Kind is ProbeKind.Unrecognized)
        {
            return Unrecognized(outcome.Text);
        }

        string reported = outcome.Result.StandardErrorLines.Count > 0
            ? string.Join('\n', outcome.Result.StandardErrorLines)
            : $"exit {outcome.Result.ExitCode}";
        return new TmuxProtocolException(
            "The tmux version probe failed: " + reported,
            reported,
            TmuxDispatchState.NotDispatched);
    }

    private async Task<bool> IsAbsentAsync(CancellationToken cancellationToken)
    {
        await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        ProbeOutcome outcome = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (outcome.Kind is not ProbeKind.Absent)
        {
            return false;
        }

        Forget(Volatile.Read(ref _supported));
        return true;
    }

    private async Task<ProbeOutcome> ProbeAsync(CancellationToken cancellationToken)
    {
        string marker = NewMarker() + "-";
        TmuxCommandResult result = await _probe!(ProbeRequest(marker), cancellationToken)
            .ConfigureAwait(false);
        ProbeOutcome outcome = ReadProbe(result, marker);
        switch (outcome.Kind)
        {
            case ProbeKind.DaemonTooOld:
                throw Refusal(
                    $"the connected daemon is older than tmux 3.2 (it cannot run the version check)"
                    + $" and is at most {outcome.Version}.",
                    outcome.Version);
            case ProbeKind.ClientTooOld:
                // The executable names its own version; it is below the 3.2
                // that knows -N, so no daemon can be reached through it.
                string client = await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
                TmuxVersion executable = ReadClientVersion(client);
                throw Refusal(
                    $"the executable reports {executable} and cannot run the version check.",
                    executable);
            default:
                return outcome;
        }
    }

    /// <summary>Reads what replaced a checked daemon, refusing one below the minimum.</summary>
    private async Task<ProbeOutcome> ReplacementAsync(
        Supported replaced,
        CancellationToken cancellationToken)
    {
        Forget(replaced);
        ProbeOutcome now = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (now.Kind is ProbeKind.Version)
        {
            Require(now.Version, "the replacement daemon");
            Volatile.Write(ref _supported, new Supported(now.Version, FromDaemon: true));
        }

        return now;
    }

    private static TmuxTransportException Changed(
        Supported supported,
        ProbeOutcome now,
        IReadOnlyList<IReadOnlyList<string>> commands) =>
        new(
            $"The tmux daemon changed from {supported.Version.Raw} to "
            + (now.Kind is ProbeKind.Version ? now.Version.Raw : "an unknown version")
            + " after its version was checked; the command did not run.",
            TmuxCommandRequest.Group([.. commands]).LogicalArguments,
            TmuxDispatchState.NotDispatched);

    private TmuxVersion ReadClientVersion(string rawVersion) =>
        TmuxVersionBannerParser.TryReadVersion(rawVersion, ExecutableName, out TmuxVersion version)
        && TmuxVersion.TryParseReported(version.Raw, out _)
            ? version
            : throw Unrecognized(rawVersion);

    private void Forget(Supported? supported) =>
        Interlocked.CompareExchange(ref _supported, null, supported);

    private sealed record Supported(TmuxVersion Version, bool FromDaemon);
}
