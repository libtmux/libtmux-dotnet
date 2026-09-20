using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

public sealed partial class Server
{
    /// <summary>Checks a layout's syntax without contacting a server.</summary>
    /// <param name="layout">A custom layout string or a named layout.</param>
    /// <param name="paneCount">The number of panes the layout must accommodate.</param>
    /// <returns>
    /// <see langword="true" /> when the checksum, cell tree and cell count are
    /// consistent for a custom layout, or the name is an unambiguous prefix of
    /// a named layout.
    /// </returns>
    /// <remarks>
    /// This is the version-independent half of <see cref="ValidateLayoutsAsync" />:
    /// it never contacts tmux, so it cannot catch a named layout that only some
    /// daemon versions accept. Call it at parse time, before a document is
    /// otherwise trusted; call <see cref="ValidateLayoutsAsync" /> afterward to
    /// catch a version-sensitive name.
    /// </remarks>
    public static bool IsValidLayoutCandidate(string layout, int paneCount)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return TmuxLayoutSyntax.IsValidCandidate(layout, paneCount);
    }

    /// <summary>Checks layouts before a workspace changes the server.</summary>
    /// <param name="layouts">Layouts and the number of panes each must accommodate.</param>
    /// <param name="cancellationToken">Cancels version discovery.</param>
    /// <returns>A task completing when every layout is safe to send.</returns>
    /// <remarks>
    /// Checks custom syntax, checksums, and minimum cell counts without checking
    /// geometry. Version-sensitive names use the daemon version, or the client
    /// version when no daemon is running. This never starts or changes a server.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The layouts collection is null.</exception>
    /// <exception cref="ArgumentException">A layout or pane count is invalid.</exception>
    /// <exception cref="InvalidDataException">The daemon version reply is malformed.</exception>
    /// <exception cref="OperationCanceledException">Validation was canceled.</exception>
    /// <exception cref="StaleServerGenerationException">The daemon changed.</exception>
    /// <exception cref="TmuxCommandException">The daemon version query failed.</exception>
    [UnsupportedOSPlatform("windows")]
    public Task ValidateLayoutsAsync(
        IEnumerable<(string Layout, int PaneCount)> layouts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        return ValidateLayoutsCoreAsync(
            [.. layouts.Select(static item => (item.Layout, item.PaneCount, (WindowId?)null))],
            _generation,
            cancellationToken);
    }

    [UnsupportedOSPlatform("windows")]
    private Task ValidateChainedLayoutsAsync(
        IReadOnlyList<TmuxCommand> commands,
        ServerGeneration? generation,
        CancellationToken cancellationToken) =>
        ValidateLayoutsCoreAsync(
            [.. commands.Where(static command => command.LayoutWindowId is not null)
                .Select(static command => (command.Arguments[^1], 1, command.LayoutWindowId))],
            generation,
            cancellationToken);

    [UnsupportedOSPlatform("windows")]
    private async Task ValidateLayoutsCoreAsync(
        (string Layout, int PaneCount, WindowId? Window)[] layouts,
        ServerGeneration? generation,
        CancellationToken cancellationToken)
    {
        static Exception Invalid(string message, WindowId? window) => window is WindowId id
            ? new TmuxWindowException(message, id, TmuxDispatchState.NotDispatched)
            : new ArgumentException(message, nameof(layouts));

        foreach ((string layout, int paneCount, WindowId? window) in layouts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paneCount < 1 || layout is null
                || !TmuxLayoutSyntax.IsValidCandidate(layout, paneCount))
            {
                throw Invalid(
                    $"Layout '{layout}' is unknown, ambiguous, malformed, or has fewer cells than panes.",
                    window);
            }
        }

        string[] sensitive = [.. layouts.Select(static item => item.Layout)
            .Where(TmuxLayoutSyntax.NeedsVersion).Distinct(StringComparer.Ordinal)];
        if (sensitive.Length == 0)
        {
            return;
        }

        TmuxVersion version = await ReadLayoutVersionAsync(generation, cancellationToken).ConfigureAwait(false);
        TmuxCapabilityState mirrors = TmuxCapabilities.GetState(version, "layout_mirrors", inferPrerelease: true);
        foreach (string layout in sensitive)
        {
            if (layout.StartsWith('{')
                ? !TmuxLayoutSyntax.SupportsJsonLayout(version)
                : mirrors == TmuxCapabilityState.Unknown
                    || !TmuxLayoutSyntax.IsNamedLayout(layout, mirrors == TmuxCapabilityState.Supported))
            {
                throw Invalid(
                    TmuxLayoutSyntax.InvalidLayoutMessage(
                        layout, mirrors == TmuxCapabilityState.Supported, $"tmux {version.Raw}"),
                    layouts.First(item => item.Layout == layout).Window);
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<TmuxVersion> ReadLayoutVersionAsync(
        ServerGeneration? generation,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await ExecuteCommandAsync(
                ["display-message", "-p", "#{pid}:#{start_time}\t#{version}"],
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || result.StandardErrorLines.Count != 0)
        {
            if (generation is null && _connection is not null && IsColdLayoutEndpoint(result))
            {
                string banner = await _connection.ReadClientVersionAsync(cancellationToken)
                    .ConfigureAwait(false);
                return TmuxVersion.Parse(banner[5..]);
            }

            TmuxCommandFailure.ThrowIfFailed(result, "Layout version discovery");
        }

        string[] fields = result.StandardOutputLines.Count == 1
            ? result.StandardOutputLines[0].Split('\t') : [];
        if (fields.Length != 2 || !TmuxVersion.TryParse(fields[1], out TmuxVersion version))
        {
            throw new InvalidDataException("tmux did not report a valid layout version.");
        }

        ServerGeneration observed = TmuxConnection.ParseGeneration(fields[0]);
        if (generation is ServerGeneration expected && expected != observed)
        {
            throw new StaleServerGenerationException(
                "The tmux server generation changed before layout validation.", expected, observed);
        }

        return version;
    }

    private bool IsColdLayoutEndpoint(TmuxCommandResult result)
    {
        if (result.ExitCode != 1 || result.StandardOutputLines.Count != 0
            || result.StandardErrorLines.Count != 1)
        {
            return false;
        }

        string error = result.StandardErrorLines[0];
        string? path = _connection?.ResolvedSocket.SocketPath;
        if (path is not null)
        {
            return error == $"no server running on {path}"
                || error == $"error connecting to {path} (No such file or directory)"
                || error == $"error connecting to {path} (Connection refused)";
        }

        return error.StartsWith("no server running on /", StringComparison.Ordinal)
            || (error.StartsWith("error connecting to /", StringComparison.Ordinal)
                && (error.EndsWith(" (No such file or directory)", StringComparison.Ordinal)
                    || error.EndsWith(" (Connection refused)", StringComparison.Ordinal)));
    }
}
