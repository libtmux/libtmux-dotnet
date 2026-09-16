using LibTmux.Internal;

#pragma warning disable CA1416

namespace LibTmux;

/// <summary>Reads one isolated, single-session psmux namespace.</summary>
/// <remarks>
/// This type exposes only the query operations audited for the pinned psmux
/// build. It is intentionally separate from <see cref="Server" />, whose
/// lifecycle, mutation, chaining, and control-mode contracts require real tmux.
/// Each observation is bound to the session generation seen at connection time.
/// Use <see cref="RefreshAsync" /> to observe a replacement session.
/// </remarks>
public sealed class PsmuxServer
{
    private readonly Server _inner;

    internal PsmuxServer(PsmuxConnectionOptions options, Server inner)
    {
        ConnectionOptions = options;
        _inner = inner;
    }

    /// <summary>Gets the exact psmux source commit accepted by this preview.</summary>
    public const string SupportedCommit = PsmuxCompatibility.SupportedCommit;

    /// <summary>Gets the exact psmux client executable SHA-256 accepted by this preview.</summary>
    public const string SupportedBinarySha256 = PsmuxCompatibility.SupportedBinarySha256;

    /// <summary>Gets the exact clean implementation banner accepted by this preview.</summary>
    public const string SupportedImplementationBanner =
        PsmuxCompatibility.SupportedImplementationLine;

    /// <summary>Gets the connection settings used for this observation.</summary>
    public PsmuxConnectionOptions ConnectionOptions { get; }

    /// <summary>Gets the psmux compatibility version reported at connection time.</summary>
    public TmuxVersion Version => _inner.Version
        ?? throw new InvalidDataException("The connected psmux client reported no usable version.");

    /// <summary>Connects to a separately provisioned psmux namespace.</summary>
    /// <param name="options">The executable trust and isolated endpoint settings.</param>
    /// <param name="cancellationToken">Cancels process startup and discovery.</param>
    /// <returns>A query-only psmux server observation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The selected namespace has no live session.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The executable, build, namespace, session count, or requested behavior is outside
    /// the audited preview contract.
    /// </exception>
    /// <exception cref="LibTmuxException">The verified client could not complete discovery.</exception>
    public static async Task<PsmuxServer> ConnectAsync(
        PsmuxConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Server inner = await Server
            .ConnectAsync(ServerConnectionOptions.ForPsmux(options), cancellationToken)
            .ConfigureAwait(false);
        var connected = new PsmuxServer(options, inner);

        // Validate the public projection before the endpoint escapes.
        _ = await connected.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        return connected;
    }

    /// <summary>Reads the sole visible session.</summary>
    /// <param name="cancellationToken">Cancels the psmux query.</param>
    /// <returns>The current immutable session observation.</returns>
    /// <exception cref="InvalidOperationException">
    /// The selected namespace no longer has one live session.
    /// </exception>
    /// <exception cref="StaleServerGenerationException">
    /// The sole session was replaced after this server was connected.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The selected namespace contains more than one session.
    /// </exception>
    /// <exception cref="LibTmuxException">The verified client could not complete the query.</exception>
    public async Task<PsmuxSession> GetSessionAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Session> sessions = await _inner
            .GetSessionsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (sessions.Count != 1)
        {
            throw new InvalidOperationException(
                $"The psmux preview requires exactly one visible session; found {sessions.Count}.");
        }

        return new PsmuxSession(this, sessions[0]);
    }

    /// <summary>Reads every window in the sole session.</summary>
    /// <param name="cancellationToken">Cancels the psmux query.</param>
    /// <returns>Current immutable window observations.</returns>
    /// <exception cref="InvalidOperationException">
    /// The selected namespace has no live session.
    /// </exception>
    /// <exception cref="StaleServerGenerationException">
    /// The sole session was replaced after this server was connected.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The selected namespace contains more than one session.
    /// </exception>
    /// <exception cref="LibTmuxException">The verified client could not complete the query.</exception>
    public async Task<IReadOnlyList<PsmuxWindow>> GetWindowsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Window> windows = await _inner
            .GetWindowsAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. windows.Select(window => new PsmuxWindow(this, window))];
    }

    /// <summary>Reads every pane in the sole session.</summary>
    /// <param name="cancellationToken">Cancels the psmux query.</param>
    /// <returns>Current immutable pane observations.</returns>
    /// <exception cref="InvalidOperationException">
    /// The selected namespace has no live session.
    /// </exception>
    /// <exception cref="StaleServerGenerationException">
    /// The sole session was replaced after this server was connected.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The selected namespace contains more than one session.
    /// </exception>
    /// <exception cref="LibTmuxException">The verified client could not complete the query.</exception>
    public async Task<IReadOnlyList<PsmuxPane>> GetPanesAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Pane> panes = await _inner
            .GetPanesAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. panes.Select(pane => new PsmuxPane(this, pane))];
    }

    /// <summary>Reconnects and returns a fresh server observation.</summary>
    /// <param name="cancellationToken">Cancels process startup and discovery.</param>
    /// <returns>A replacement observation using the same endpoint settings.</returns>
    /// <exception cref="InvalidOperationException">
    /// The selected namespace has no live session.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The executable, build, namespace, or session count is outside the preview contract.
    /// </exception>
    /// <exception cref="LibTmuxException">The verified client could not complete discovery.</exception>
    public Task<PsmuxServer> RefreshAsync(CancellationToken cancellationToken = default) =>
        ConnectAsync(ConnectionOptions, cancellationToken);
}

#pragma warning restore CA1416
