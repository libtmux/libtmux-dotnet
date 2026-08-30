using System.Runtime.Versioning;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux;

// Session mutations return replacement handles; stale handles remain immutable
// observations of what was read.
public sealed partial class Session
{
    private const string GroupKillCapability = "kill_session_group";

    /// <summary>Gets the session name captured with this handle.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The session was resolved by identifier rather than materialized.
    /// </exception>
    public string Name =>
        ReadSnapshot("session_name")
        ?? throw new IncompleteSnapshotException("name", SnapshotDepth.Sessions);

    /// <summary>Gets whether a client was attached when this session was read.</summary>
    /// <exception cref="IncompleteSnapshotException">
    /// The session was resolved by identifier rather than materialized.
    /// </exception>
    /// <remarks>
    /// This is captured state rather than a live one: it says what tmux
    /// reported when the handle was made, which is what makes a reading of a
    /// hierarchy consistent with itself.
    /// </remarks>
    public bool Attached =>
        ReadSnapshot("session_attached")
        is string attached
            ? attached is not ("" or "0")
            : throw new IncompleteSnapshotException("attached", SnapshotDepth.Sessions);

    /// <summary>Gets the server that owns this session.</summary>
    /// <remarks>
    /// Every handle reached through a server carries it, whether the handle was
    /// materialized from a listing or resolved from an identifier.
    /// </remarks>
    public Server Server => RequireOwner("server");

    /// <summary>Re-reads this session from tmux.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying current state.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Session> RefreshAsync(CancellationToken cancellationToken = default)
    {
        Server owner = RequireOwner("refresh");
        IReadOnlyDictionary<string, string?> row = await RelationReader
            .FindAsync(
                owner,
                "list-sessions",
                "session_id",
                _id.ToString(),
                inSession: null,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TmuxObjectNotFoundException(
                $"tmux no longer has session '{_id}'.",
                _id.ToString());
        return RelationReader.ToSession(owner, row);
    }

    /// <summary>Renames this session.</summary>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the new name.</returns>
    /// <remarks>
    /// tmux expands the name as a format, so a <c>#</c> in it does not survive
    /// verbatim.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Session> RenameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        SessionName.Validate(name);
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["rename-session", "-t", _id.ToString(), name], cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Stops this session.</summary>
    /// <param name="allExcept">Whether every other session is stopped instead of this one.</param>
    /// <param name="clearAlerts">Whether alerts are cleared in every window instead.</param>
    /// <param name="group">Whether every session in this session's group is stopped.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <exception cref="IncompleteSnapshotException">
    /// <paramref name="group" /> is set on a handle resolved by identifier,
    /// which carries no server to read a version from.
    /// </exception>
    /// <remarks>
    /// Group stopping arrived in tmux 3.7. Older servers reject the flag and
    /// stop nothing at all, so against those the request is logged and the flag
    /// omitted: this session still dies, its group siblings do not.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task KillAsync(
        bool allExcept = false,
        bool clearAlerts = false,
        bool group = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["kill-session"];
        if (allExcept)
        {
            arguments.Add("-a");
        }

        if (clearAlerts)
        {
            arguments.Add("-C");
        }

        if (group && SupportsGroupKill())
        {
            arguments.Add("-g");
        }

        arguments.Add("-t");
        arguments.Add(_id.ToString());
        return RunAsync(arguments, cancellationToken);
    }

    /// <summary>Locks this session.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task LockAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["lock-session", "-t", _id.ToString()], cancellationToken);

    /// <summary>Detaches every client attached to this session.</summary>
    /// <param name="shellCommand">A command the detached clients run, when any.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <remarks>
    /// The session scope is always sent, because <c>-s</c> is the only
    /// <c>detach-client</c> flag group that names a session rather than a
    /// client.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public Task DetachClientAsync(
        string? shellCommand = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            shellCommand is null
                ? ["detach-client", "-s", _id.ToString()]
                : ["detach-client", "-s", _id.ToString(), "-E", shellCommand],
            cancellationToken);

    /// <summary>Selects a window in this session.</summary>
    /// <param name="target">The window target.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The window that is active afterwards.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> SelectWindowAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["select-window", "-t", Scoped(target)], cancellationToken),
                () => ActiveWindowAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Selects the next window.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The window that is active afterwards.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> SelectNextWindowAsync(CancellationToken cancellationToken = default)
    {
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["next-window", "-t", _id.ToString()], cancellationToken),
                () => ActiveWindowAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Selects the previous window.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The window that is active afterwards.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> SelectPreviousWindowAsync(
        CancellationToken cancellationToken = default)
    {
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["previous-window", "-t", _id.ToString()], cancellationToken),
                () => ActiveWindowAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Stops one window in this session.</summary>
    /// <param name="target">The window target, or null for the active window.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    [UnsupportedOSPlatform("windows")]
    public Task KillWindowAsync(
        string? target = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            ["kill-window", "-t", target is null ? _id.ToString() : Scoped(target)],
            cancellationToken);

    /// <summary>Switches the current client to this session.</summary>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A replacement handle carrying the state after the switch.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Session> SwitchClientAsync(CancellationToken cancellationToken = default)
    {
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(["switch-client", "-t", _id.ToString()], cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Runs a tmux-side filter over this session's windows.</summary>
    /// <param name="filter">The raw tmux filter expression.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The windows tmux kept.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Window>> SearchWindowsAsync(
        UnsafeTmuxFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Server owner = RequireOwner("windows");
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await RelationReader
            .ListAsync(
                owner,
                "list-windows",
                ["-t", _id.ToString(), "-f", filter.Value],
                cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => RelationReader.ToWindow(owner, row))];
    }

    /// <summary>Runs a tmux-side filter over this session's panes.</summary>
    /// <param name="filter">The raw tmux filter expression.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The panes tmux kept.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<Pane>> SearchPanesAsync(
        UnsafeTmuxFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Server owner = RequireOwner("panes");
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await RelationReader
            .ListAsync(
                owner,
                "list-panes",
                ["-s", "-t", _id.ToString(), "-f", filter.Value],
                cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => RelationReader.ToPane(owner, row))];
    }

    /// <summary>Attaches a client to this session.</summary>
    /// <param name="request">Attachment options.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>A refreshed handle for this session.</returns>
    /// <remarks>
    /// Attaching needs a terminal, so this fails outside one rather than
    /// silently doing nothing.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public async Task<Session> AttachAsync(
        AttachSessionRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        AttachSessionRequest options = request ?? new AttachSessionRequest();
        return await TmuxMutationSequence.RunAsync(
                () => RunAsync(
                    [.. BuildAttachArguments(options, _id.ToString())],
                    cancellationToken),
                () => RefreshAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Creates a window in this session.</summary>
    /// <param name="request">The window to create.</param>
    /// <param name="cancellationToken">Cancels the tmux command.</param>
    /// <returns>The created window.</returns>
    [UnsupportedOSPlatform("windows")]
    public async Task<Window> CreateWindowAsync(
        NewWindowRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        NewWindowRequest options = request ?? new NewWindowRequest();
        Server owner = RequireOwner("windows");
        bool maySelectExisting = options.SelectExisting
            && options.Name is not null
            && options.Index is null
            && options.TargetWindow is null;
        string? selectedName = maySelectExisting
            ? await ExpandWindowNameAsync(options.Name!, cancellationToken).ConfigureAwait(false)
            : null;
        var sequence = new TmuxMutationSequence();
        TmuxCommandResult result = await sequence.MutateAsync(
                () => _commandDispatcher.ExecuteAsync(
                    [.. BuildNewWindowArguments(options, _id.ToString())],
                    cancellationToken),
                static value => TmuxCommandFailure.ThrowIfFailed(value, "new-window"))
            .ConfigureAwait(false);

        if (result.StandardOutputLines.Count == 0 && selectedName is not null)
        {
            IReadOnlyList<Window> selectedWindows = await sequence
                .ObserveAsync(() => GetWindowsAsync(cancellationToken))
                .ConfigureAwait(false);
            return sequence.Observe(() =>
            {
                Window[] matches =
                [.. selectedWindows.Where(window =>
                    string.Equals(window.Name, selectedName, StringComparison.Ordinal))];
                return matches.Length == 1
                    ? matches[0]
                    : throw new InvalidDataException(
                        $"tmux did not report exactly one selected window named '{selectedName}'.");
            });
        }

        WindowId created = sequence.Observe(() =>
            result.StandardOutputLines.Count > 0
                && WindowId.TryParse(result.StandardOutputLines[0], out WindowId parsed)
                    ? parsed
                    : throw new InvalidDataException("tmux reported no new window identifier."));

        IReadOnlyList<Window> windows = await sequence
            .ObserveAsync(() => owner.GetWindowsAsync(cancellationToken))
            .ConfigureAwait(false);
        return sequence.Observe(() =>
            windows.FirstOrDefault(window => window.Id == created)
                ?? throw new TmuxObjectNotFoundException(
                    $"tmux did not report the created window '{created}'.",
                    created.ToString()));
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<string> ExpandWindowNameAsync(
        string name,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _commandDispatcher.ExecuteAsync(
                ["display-message", "-p", "-t", _id.ToString(), "--", name],
                cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, "display-message");
        return result.StandardOutputLines.Count == 1
            ? result.StandardOutputLines[0]
            : throw new InvalidDataException(
                "tmux did not report exactly one expanded window name.");
    }

    internal static IEnumerable<string> BuildAttachArguments(
        AttachSessionRequest options,
        string fallbackTarget)
    {
        yield return "attach-session";
        yield return "-t";
        yield return options.Target ?? fallbackTarget;
        if (options.DetachOthers)
        {
            yield return "-d";
        }

        if (options.ReadOnly)
        {
            yield return "-r";
        }

        if (options.ExitOnDetach)
        {
            yield return "-x";
        }

        // tmux reads -f once and keeps the last value, so repeated flags would
        // silently discard every flag but one; it wants a comma-separated list.
        if (options.ClientFlags is { Count: > 0 } clientFlags)
        {
            yield return "-f";
            yield return string.Join(',', clientFlags);
        }
    }

    internal static IEnumerable<string> BuildNewWindowArguments(
        NewWindowRequest options,
        string sessionId)
    {
        yield return "new-window";
        yield return "-P";
        yield return "-F";
        yield return "#{window_id}";
        if (!options.Attach)
        {
            yield return "-d";
        }

        if (options.KillExisting)
        {
            yield return "-k";
        }

        if (options.SelectExisting)
        {
            yield return "-S";
        }

        if (options.Direction is WindowDirection direction)
        {
            yield return CommandFlagCatalog.GetWindowDirectionFlag(direction);
        }

        // A bare index or window name would resolve against the caller's
        // current session, so the target is always anchored to this session.
        yield return "-t";
        yield return options.TargetWindow is not null
            ? $"{sessionId}:{options.TargetWindow}"
            : options.Index is null
                ? $"{sessionId}:"
                : $"{sessionId}:{options.Index}";
        foreach ((string flag, string? value) in new[]
        {
            ("-n", options.Name),
            ("-c", StartDirectory.Resolve(options.StartDirectory)),
        })
        {
            if (value is not null)
            {
                yield return flag;
                yield return value;
            }
        }

        if (options.Environment is not null)
        {
            foreach ((string key, string value) in options.Environment)
            {
                yield return "-e";
                yield return $"{key}={value}";
            }
        }

        if (options.Command is not null)
        {
            yield return options.Command;
        }
    }

    // The version comes from state captured when the handle materialized, so
    // gating costs no extra tmux command and the kill still dispatches once.
    private bool SupportsGroupKill()
    {
        Server owner = RequireOwner("group");
        if (owner.Version is TmuxVersion version
            && TmuxCapabilities.IsSupported(version, GroupKillCapability))
        {
            return true;
        }

        if (owner.Connection?.Options.Logger is ILogger logger)
        {
            LogGroupKillUnsupported(logger, owner.RawVersion);
        }

        return false;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "session group kill flag omitted, tmux {TmuxVersion} does not carry it")]
    private static partial void LogGroupKillUnsupported(ILogger logger, string? tmuxVersion);

    // Selection reports the window tmux settled on rather than the one that was
    // asked for, because next-window and previous-window choose it themselves.
    [UnsupportedOSPlatform("windows")]
    private async Task<Window> ActiveWindowAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Window> windows = await GetWindowsAsync(cancellationToken)
            .ConfigureAwait(false);
        return windows.FirstOrDefault(window => window.Snapshot?["window_active"] == "1")
            ?? throw new TmuxObjectNotFoundException(
                $"tmux reported no active window in session '{_id}'.",
                _id.ToString());
    }

    // Always anchor: tmux resolves bare names in the current session, while ':'
    // may be part of a window name rather than an already-qualified target.
    private string Scoped(string target) => $"{_id}:{target}";

    [UnsupportedOSPlatform("windows")]
    private async Task RunAsync(
        List<string> arguments,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await _commandDispatcher
            .ExecuteAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        TmuxCommandFailure.ThrowIfFailed(result, arguments[0]);
    }
}

/// <summary>Owns a session and stops it when disposed.</summary>
public sealed class OwnedSessionScope : IAsyncDisposable
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private int _disposed;

    internal OwnedSessionScope(Session value) => Value = value;

    /// <summary>Gets the owned session.</summary>
    public Session Value { get; }

    /// <summary>Stops the owned session.</summary>
    /// <returns>A task that completes once the session is gone.</returns>
    /// <exception cref="LibTmuxException">The session could not be stopped.</exception>
    [UnsupportedOSPlatform("windows")]
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using CancellationTokenSource cleanup = new(CleanupTimeout);
        try
        {
            await Value.KillAsync(cancellationToken: cleanup.Token).ConfigureAwait(false);
        }
        catch (TmuxCommandException error) when (NamesAbsentSession(error.Result))
        {
            // A session the server already dropped is the outcome that was
            // asked for. Anything else is surfaced: disposal that quietly fails
            // to clean up leaves a live session behind.
        }
    }

    private static bool NamesAbsentSession(TmuxCommandResult result) =>
        result.StandardErrorLines.Any(static line =>
            line.Contains("can't find session", StringComparison.Ordinal)
            || line.Contains("no server running", StringComparison.Ordinal));
}
