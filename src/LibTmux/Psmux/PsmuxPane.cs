#pragma warning disable CA1416

namespace LibTmux;

/// <summary>An immutable observation of one psmux pane.</summary>
/// <remarks>
/// This observation is bound to <see cref="Server" />'s captured generation.
/// Queries throw <see cref="StaleServerGenerationException" /> after replacement;
/// call <see cref="PsmuxServer.RefreshAsync" /> to obtain a fresh observation.
/// </remarks>
public sealed class PsmuxPane
{
    private readonly Pane _inner;

    internal PsmuxPane(PsmuxServer server, Pane inner)
    {
        Server = server;
        _inner = inner;
    }

    /// <summary>Gets the psmux endpoint that produced this observation.</summary>
    public PsmuxServer Server { get; }

    /// <summary>Gets the captured pane identifier.</summary>
    public PaneId Id => _inner.Id;

    /// <summary>Gets the captured parent session identifier.</summary>
    public SessionId SessionId => SessionId.Parse(ReadRequired("session_id"));

    /// <summary>Gets the captured parent window identifier.</summary>
    public WindowId WindowId => WindowId.Parse(ReadRequired("window_id"));

    /// <summary>Gets the captured pane index.</summary>
    public int Index => _inner.Index;

    /// <summary>Gets the captured width in columns.</summary>
    public int Width => _inner.Width;

    /// <summary>Gets the captured height in rows.</summary>
    public int Height => _inner.Height;

    /// <summary>Gets the captured pane title.</summary>
    public string? Title => _inner.Title;

    /// <summary>Reads this pane's text through the audited capture subset.</summary>
    /// <param name="options">The typed capture range and rendering choices.</param>
    /// <param name="cancellationToken">Cancels the psmux query.</param>
    /// <returns>The captured lines.</returns>
    /// <remarks>
    /// Target consistency is best effort. An external process can remove the
    /// pane between LibTmux's existence preflight and psmux's capture.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The selected namespace has no live session.
    /// </exception>
    /// <exception cref="StaleServerGenerationException">
    /// The sole session was replaced after this observation was read.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The selected namespace contains more than one session.
    /// </exception>
    /// <exception cref="TmuxObjectNotFoundException">
    /// The observed pane is no longer visible during the preflight.
    /// </exception>
    /// <exception cref="LibTmuxException">The verified client could not complete the query.</exception>
    public Task<IReadOnlyList<string>> CaptureAsync(
        PsmuxCaptureOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _inner.CaptureAsync(options?.ToRequest(), cancellationToken);

    private string ReadRequired(string name) =>
        _inner.RawFormatFields.TryGetValue(name, out string? value)
            && !string.IsNullOrEmpty(value)
                ? value
                : throw new TmuxProtocolException(
                    $"The psmux pane row omitted {name}.",
                    TmuxDispatchState.Dispatched);
}

#pragma warning restore CA1416
