using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

// Provides session hierarchy relations.
public sealed partial class Session
{
    private readonly Server? _owner;
    private Func<CapturedRelation<Window>>? _windows;
    private CapturedRelation<Pane>? _panes;

    /// <summary>Gets the captured active window, or an uncaptured relation.</summary>
    /// <remarks>
    /// Reading this is local. A session reached through an inactive window
    /// carries that window's row, so its active window was not captured.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public CapturedRelation<Window> ActiveWindow =>
        ReadSnapshot("window_active") == "1"
            ? CapturedRelation.Capture(
                [ReadActiveWindow()],
                "active window",
                SnapshotDepth.Sessions)
            : CapturedRelation.Uncaptured<Window>("active window", SnapshotDepth.Sessions);

    /// <summary>Gets the captured active pane, or an uncaptured relation.</summary>
    /// <remarks>
    /// Reading this is local. The row must describe the active pane in the
    /// session's active window; other rows leave this relation uncaptured.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public CapturedRelation<Pane> ActivePane =>
        ReadSnapshot("window_active") == "1" && ReadSnapshot("pane_active") == "1"
            ? CapturedRelation.Capture(
                [ReadActivePane()],
                "active pane",
                SnapshotDepth.Sessions)
            : CapturedRelation.Uncaptured<Pane>("active pane", SnapshotDepth.Sessions);

    /// <summary>Gets the windows the capture found in this session.</summary>
    /// <remarks>
    /// Reading this never reaches tmux. A handle that was not read from a
    /// capture answers uncaptured rather than empty, because "nobody looked"
    /// and "there are none" are different answers.
    /// </remarks>
    public CapturedRelation<Window> Windows =>
        _windows?.Invoke() ?? CapturedRelation.Uncaptured<Window>("windows", SnapshotDepth.Server);

    /// <summary>Gets the panes the capture found in this session.</summary>
    /// <remarks>Reading this never reaches tmux.</remarks>
    public CapturedRelation<Pane> Panes =>
        _panes ?? CapturedRelation.Uncaptured<Pane>("panes", SnapshotDepth.Server);

    /// <summary>Reads one window in this session, throwing when it is absent.</summary>
    /// <param name="id">The window identifier.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The materialized window in this session.</returns>
    /// <exception cref="TmuxObjectNotFoundException">This session has no matching window.</exception>
    /// <exception cref="LibTmuxException">The lookup failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> GetWindowAsync(
        WindowId id,
        CancellationToken cancellationToken = default) =>
        await FindWindowAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new TmuxObjectNotFoundException(
            $"Session {_id} has no window '{id}'.", id.ToString());

    /// <summary>Reads one window in this session, throwing when it is absent.</summary>
    /// <param name="target">The window identifier or name.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The materialized window in this session.</returns>
    /// <exception cref="TmuxObjectNotFoundException">This session has no matching window.</exception>
    /// <exception cref="LibTmuxException">The lookup failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> GetWindowAsync(
        string target,
        CancellationToken cancellationToken = default) =>
        await FindWindowAsync(target, cancellationToken).ConfigureAwait(false)
        ?? throw new TmuxObjectNotFoundException(
            $"Session {_id} has no window '{target}'.", target);

    /// <summary>Reads one of this session's windows by identifier.</summary>
    /// <param name="id">The window identifier to look for.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The window, or null when this session has no such window.</returns>
    /// <remarks>
    /// A typed identifier names exactly one window. The string overload also
    /// accepts a window name, which two windows in one session can share, so
    /// prefer this one where the identifier is already in hand.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window?> FindWindowAsync(
        WindowId id,
        CancellationToken cancellationToken = default)
    {
        Server owner = RequireOwner("windows");
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
            await RelationReader.ListAsync(
                owner,
                "list-windows",
                ["-t", _id.ToString()],
                cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyDictionary<string, string?>? match = rows.FirstOrDefault(
            row => Matches(row, "window_id", id.ToString()));
        return match is null ? null : RelationReader.ToWindow(owner, match);
    }

    /// <summary>Reads one of this session's windows by target.</summary>
    /// <param name="target">A tmux window target inside this session.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The window, or null when this session has no such window.</returns>
    /// <remarks>
    /// A target naming a window in another session answers null rather than
    /// that window: the question asked is which of this session's windows it
    /// is, and tmux resolving it elsewhere is not an answer to that.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window?> FindWindowAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        Server owner = RequireOwner("windows");
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
            await RelationReader.ListAsync(
                owner,
                "list-windows",
                ["-t", _id.ToString()],
                cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyDictionary<string, string?>? match = rows.FirstOrDefault(row =>
            Matches(row, "window_id", target) || Matches(row, "window_name", target));
        return match is null ? null : RelationReader.ToWindow(owner, match);
    }

    private static bool Matches(
        IReadOnlyDictionary<string, string?> row,
        string wireName,
        string target) =>
        row.TryGetValue(wireName, out string? value)
        && string.Equals(value, target, StringComparison.Ordinal);

    /// <summary>Reads this session's windows from tmux.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The windows tmux reports for this session.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Window>> GetWindowsAsync(
        CancellationToken cancellationToken = default)
    {
        Server owner = RequireOwner("windows");
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
            await RelationReader.ListAsync(owner, "list-windows", ["-t", _id.ToString()], cancellationToken)
                .ConfigureAwait(false);
        return [.. rows.Select(row => RelationReader.ToWindow(owner, row))];
    }

    /// <summary>Reads this session's panes from tmux.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The panes tmux reports for this session.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Pane>> GetPanesAsync(
        CancellationToken cancellationToken = default)
    {
        Server owner = RequireOwner("panes");
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows =
            await RelationReader.ListAsync(owner, "list-panes", ["-s", "-t", _id.ToString()], cancellationToken)
                .ConfigureAwait(false);
        return [.. rows.Select(row => RelationReader.ToPane(owner, row))];
    }

    internal Session WithCaptured(
        Func<CapturedRelation<Window>> windows,
        CapturedRelation<Pane> panes)
    {
        _windows = windows;
        _panes = panes;
        return this;
    }

    [UnsupportedOSPlatform("windows")]
    private Window ReadActiveWindow()
    {
        CapturedRelation<Window> windows = Windows;
        Window? captured = windows.IsCaptured
            ? windows.FirstOrDefault(window => window.Id.ToString() == ReadSnapshot("window_id")
                && window.RawFormatFields.GetValueOrDefault("window_index") == ReadSnapshot("window_index"))
            : null;
        return captured ?? RelationReader.ToWindow(RequireOwner("active window"), RawFormatFields);
    }

    [UnsupportedOSPlatform("windows")]
    private Pane ReadActivePane()
    {
        CapturedRelation<Pane> panes = Panes;
        Pane? captured = panes.IsCaptured
            ? panes.FirstOrDefault(pane => pane.Id.ToString() == ReadSnapshot("pane_id")
                && pane.RawFormatFields.GetValueOrDefault("window_index") == ReadSnapshot("window_index"))
            : null;
        return captured ?? RelationReader.ToPane(RequireOwner("active pane"), RawFormatFields);
    }

    private Server RequireOwner(string relation) =>
        _owner ?? throw new IncompleteSnapshotException(relation, SnapshotDepth.Server);

    private string? ReadSnapshot(string wireName) =>
        _snapshot is not null && _snapshot.TryGetValue(wireName, out string? value)
            ? value
            : null;
}
