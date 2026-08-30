namespace LibTmux.Internal;

/// <summary>Speaks to a real tmux server.</summary>
internal sealed class TmuxDialect : MultiplexerDialect
{
    private readonly TmuxGenerationGuard _generationGuard;
    private readonly bool _processBacked;
    private readonly string _binaryPath;

    internal TmuxDialect(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> executeVersion,
        Func<string> markerFactory,
        bool processBacked,
        string binaryPath)
        : base(execute, executeVersion)
    {
        _generationGuard = new TmuxGenerationGuard(execute, markerFactory);
        _processBacked = processBacked;
        _binaryPath = binaryPath;
    }

    internal override bool IsPsmux => false;

    internal override async Task<(ServerGeneration Generation, string RawVersion)> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        string rawVersion = await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        TmuxCommandResult result = await Execute(
                TmuxCommandRequest.Single(
                    ["display-message", "-p", TmuxConnection.GenerationFormat]),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || result.StandardErrorLines.Count > 0)
        {
            throw new TmuxCommandException("server generation discovery failed.", result);
        }

        if (result.StandardOutputLines.Count != 1)
        {
            throw new InvalidDataException("tmux did not report exactly one server generation.");
        }

        return (TmuxConnection.ParseGeneration(result.StandardOutputLines[0]), rawVersion);
    }

    internal override async Task<TmuxCommandResult> ExecuteSingleAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        return await Execute(TmuxCommandRequest.Single(arguments), cancellationToken)
            .ConfigureAwait(false);
    }

    internal override async Task<TmuxCommandResult> ExecuteGroupAsync(
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken)
    {
        await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        return await Execute(TmuxCommandRequest.Group([.. commands]), cancellationToken)
            .ConfigureAwait(false);
    }

    internal override async Task<TmuxCommandResult> ExecuteGuardedAsync(
        ServerGeneration expected,
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken)
    {
        await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        return await _generationGuard.ExecuteAsync(expected, commands, cancellationToken)
            .ConfigureAwait(false);
    }

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
}
