namespace LibTmux.Internal;

/// <summary>Speaks to the psmux preview, which serves one isolated session.</summary>
/// <remarks>
/// psmux answers a subset of tmux and does not preserve grouped-command
/// semantics, so a generation is guarded by best-effort preflight rather than
/// by one invocation. Everything the preview refuses is refused here.
/// </remarks>
internal sealed class PsmuxDialect : MultiplexerDialect
{
    private readonly PsmuxSessionRouter _router;
    private readonly ServerConnectionOptions _options;
    private readonly string? _resolvedSocketName;

    internal PsmuxDialect(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> executeVersion,
        ServerConnectionOptions options,
        string? resolvedSocketName)
        : base(execute, executeVersion)
    {
        _options = options;
        _resolvedSocketName = resolvedSocketName;
        AcceptEndpoint();
        _router = new PsmuxSessionRouter(ExecuteRawSingleAsync);
    }

    internal override bool IsPsmux => true;

    internal override async Task<(ServerGeneration Generation, string RawVersion)> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        string rawVersion = await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        PsmuxSessionState session = await _router.DiscoverSessionAsync(cancellationToken)
            .ConfigureAwait(false);
        return (session.Generation, rawVersion);
    }

    internal override async Task<TmuxCommandResult> ExecuteSingleAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        return await _router.ExecuteSingleAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
    }

    internal override async Task<TmuxCommandResult> ExecuteGroupAsync(
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken)
    {
        if (commands.Count != 1)
        {
            throw new NotSupportedException(
                "psmux does not preserve tmux grouped-command semantics.");
        }

        return await ExecuteSingleAsync(commands[0], cancellationToken).ConfigureAwait(false);
    }

    internal override async Task<TmuxCommandResult> ExecuteGuardedAsync(
        ServerGeneration expected,
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken)
    {
        await EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        return await _router.ExecuteGuardedAsync(expected, commands, cancellationToken)
            .ConfigureAwait(false);
    }

    private protected override void AcceptBanner(TmuxVersionBanner banner)
    {
        if (banner.Implementation is not TmuxImplementation.Psmux)
        {
            throw new NotSupportedException(
                "The trusted psmux preview executable reported a tmux banner.");
        }

        if (!string.Equals(
                banner.Version,
                PsmuxCompatibility.SupportedVersion,
                StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"The psmux preview supports exactly version {PsmuxCompatibility.SupportedVersion}.");
        }

        if (!string.Equals(
                banner.ImplementationLine,
                PsmuxCompatibility.SupportedImplementationLine,
                StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"The psmux preview supports exactly {PsmuxCompatibility.SupportedImplementationLine}.");
        }
    }

    private protected override void AcceptEndpoint()
    {
        if (_options.SocketPath is not null)
        {
            throw new NotSupportedException(
                "psmux connections require a socket name because -S does not select a namespace.");
        }

        if (string.IsNullOrEmpty(_resolvedSocketName)
            || string.Equals(_resolvedSocketName, "default", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "psmux connections require a non-default socket name for endpoint isolation.");
        }

        PsmuxTargetGrammar.ValidateName(_resolvedSocketName, "namespace");

        if (_options.ColorMode is not TmuxColorMode.Default)
        {
            throw new NotSupportedException(
                "psmux does not honor tmux's forced client color modes.");
        }

        if (_options.ConfigurationFile is not null)
        {
            throw new NotSupportedException(
                "psmux cannot apply a per-client configuration file to a pre-existing session.");
        }
    }

    /// <summary>Runs one command, reporting it as the command the caller asked for.</summary>
    /// <remarks>
    /// The router rewrites a target before sending it, and a caller who never
    /// saw that rewrite cannot read a failure that quotes it.
    /// </remarks>
    private async Task<TmuxCommandResult> ExecuteRawSingleAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? preserveArguments = null)
    {
        TmuxCommandRequest request = TmuxCommandRequest.Single(arguments);
        TmuxCommandResult result;
        try
        {
            result = await Execute(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TmuxTransportException error) when (preserveArguments is not null)
        {
            throw new TmuxTransportException(
                error.Message,
                preserveArguments,
                error.Dispatch,
                error.InnerException);
        }

        return preserveArguments is null
            ? result
            : TmuxCommandResultProjection.Remap(
                result,
                preserveArguments,
                result.StandardOutput);
    }
}
