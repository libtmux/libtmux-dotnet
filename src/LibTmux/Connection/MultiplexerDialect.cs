namespace LibTmux.Internal;

/// <summary>The multiplexer a connection speaks to, and how it is addressed.</summary>
/// <remarks>
/// tmux and the psmux preview accept different commands, guard a generation
/// differently, and report different version banners. Choosing between them is
/// a connection's first decision rather than a question asked of every command,
/// so it is answered once here and then delegated to.
/// </remarks>
internal abstract class MultiplexerDialect
{
    private readonly Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>
        _executeVersion;
    private string? _rawVersion;

    private protected MultiplexerDialect(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> execute,
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> executeVersion)
    {
        Execute = execute;
        _executeVersion = executeVersion;
    }

    /// <summary>Gets whether this dialect speaks to the psmux preview.</summary>
    internal abstract bool IsPsmux { get; }

    /// <summary>Gets the version that governs what this dialect may send.</summary>
    /// <remarks>
    /// The executable's report unless a dialect checks a daemon, which answers
    /// for itself.
    /// </remarks>
    internal virtual string VerifiedRawVersion => ClientRawVersion;

    /// <summary>Gets the version the client executable reported.</summary>
    private protected string ClientRawVersion => Volatile.Read(ref _rawVersion)
        ?? throw new InvalidOperationException("The multiplexer version has not been verified.");

    /// <summary>Gets the file name the configured executable is known by.</summary>
    private protected abstract string ExecutableName { get; }

    /// <summary>Gets the transport every command ultimately reaches.</summary>
    private protected Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>>
        Execute
    { get; }

    /// <summary>Reads the server generation and the version banner.</summary>
    internal abstract Task<(ServerGeneration Generation, string RawVersion)> DiscoverAsync(
        CancellationToken cancellationToken);

    /// <summary>Reads the daemon's generation and version without a user-visible command.</summary>
    /// <returns>
    /// Null when this dialect has no such read, in which case a caller asks
    /// the daemon itself. A null generation means no daemon is running and the
    /// version is the executable's.
    /// </returns>
    internal virtual Task<(ServerGeneration? Generation, TmuxVersion Version)?> ReadIdentityAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<(ServerGeneration? Generation, TmuxVersion Version)?>(null);

    /// <summary>Runs one command.</summary>
    internal abstract Task<TmuxCommandResult> ExecuteSingleAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    /// <summary>Runs several commands in one invocation.</summary>
    internal abstract Task<TmuxCommandResult> ExecuteGroupAsync(
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken);

    /// <summary>Runs commands under a check that the server is still the same one.</summary>
    internal abstract Task<TmuxCommandResult> ExecuteGuardedAsync(
        ServerGeneration expected,
        IReadOnlyList<IReadOnlyList<string>> commands,
        CancellationToken cancellationToken);

    /// <summary>Accepts or rejects the banner the executable reported.</summary>
    /// <exception cref="NotSupportedException">The banner is not this dialect's.</exception>
    private protected abstract void AcceptBanner(TmuxVersionBanner banner);

    /// <summary>Rejects an endpoint this dialect cannot serve, before the first command.</summary>
    private protected virtual void AcceptEndpoint()
    {
    }

    /// <summary>Verifies the executable once, before the first command reaches it.</summary>
    /// <remarks>
    /// The banner is read through a transport carrying no endpoint arguments:
    /// <c>-V</c> answers from the client rather than from a server, and a socket
    /// that names nothing running would fail the question.
    /// </remarks>
    internal async Task<string> EnsureVerifiedAsync(CancellationToken cancellationToken)
    {
        AcceptEndpoint();
        if (Volatile.Read(ref _rawVersion) is string known)
        {
            return known;
        }

        TmuxCommandResult result = await _executeVersion(
                TmuxCommandRequest.Single(["-V"]),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || result.StandardErrorLines.Count > 0)
        {
            throw new TmuxCommandException("multiplexer version discovery failed.", result);
        }

        if (!TmuxVersionBannerParser.TryParse(
                result.StandardOutputLines,
                out TmuxVersionBanner banner,
                ExecutableName))
        {
            throw new TmuxCommandException(
                "The multiplexer did not report a recognized version banner.",
                result);
        }

        AcceptBanner(banner);

        // Two first dispatches can read the banner at once. Each is accepted on
        // its own, so the first published reading is the one they both use.
        return Interlocked.CompareExchange(ref _rawVersion, banner.RawVersion, null)
            ?? banner.RawVersion;
    }
}
