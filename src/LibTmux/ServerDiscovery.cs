using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using LibTmux.Internal;

namespace LibTmux;

/// <summary>Bounds discovery over socket directories supplied by the caller and the captured environment.</summary>
public sealed record ServerDiscoveryOptions
{
    /// <summary>Gets additional absolute directories whose immediate children are socket candidates.</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    /// <summary>Gets whether discovery includes the current user's default, configured and selected socket directories.</summary>
    public bool IncludeConfiguredRoots { get; init; } = true;

    /// <summary>Gets the connection settings used for probes, including executable and child environment.</summary>
    public ServerConnectionOptions Connection { get; init; } = new();

    /// <summary>Gets the maximum number of input root entries inspected, including duplicates.</summary>
    public int MaximumRoots { get; init; } = 16;

    /// <summary>Gets the maximum number of directory entries inspected across all roots.</summary>
    public int MaximumEntries { get; init; } = 256;

    /// <summary>Gets the maximum number of socket probes.</summary>
    public int MaximumProbes { get; init; } = 64;

    /// <summary>Gets the total discovery deadline, checked between filesystem operations and enforced during probes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the maximum duration of one no-start probe.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>Describes one skipped, failed, duplicate or bounded discovery operation.</summary>
/// <param name="Path">The root or candidate involved.</param>
/// <param name="Kind">The diagnostic category.</param>
/// <param name="Message">The observed reason.</param>
public sealed record ServerDiscoveryDiagnostic(string Path, string Kind, string Message);

/// <summary>Describes one responsive daemon found at a socket path.</summary>
/// <param name="SocketPath">The path used to probe the daemon.</param>
/// <param name="Server">The borrowed generation-bound handle.</param>
public sealed record DiscoveredServer(string SocketPath, Server Server);

/// <summary>Reports bounded discovery results without implying a machine-wide inventory.</summary>
public sealed class ServerDiscoveryResult
{
    internal ServerDiscoveryResult(List<DiscoveredServer> servers, List<ServerDiscoveryDiagnostic> diagnostics,
        bool truncated, int entries, int probes)
    {
        Servers = servers.AsReadOnly();
        Diagnostics = diagnostics.AsReadOnly();
        Truncated = truncated;
        EntriesVisited = entries;
        ProbesAttempted = probes;
    }

    /// <summary>Gets one borrowed handle per daemon generation.</summary>
    public IReadOnlyList<DiscoveredServer> Servers { get; }

    /// <summary>Gets root errors, stale sockets, skipped entries, probe failures, duplicates and exhausted bounds.</summary>
    public IReadOnlyList<ServerDiscoveryDiagnostic> Diagnostics { get; }

    /// <summary>Gets whether a root, entry, probe or time limit stopped discovery.</summary>
    public bool Truncated { get; }

    /// <summary>Gets the number of filesystem entries examined.</summary>
    public int EntriesVisited { get; }

    /// <summary>Gets the number of candidate sockets probed.</summary>
    public int ProbesAttempted { get; }
}

public sealed partial class Server
{
    /// <summary>Discovers responsive daemons within explicit and configured socket directories.</summary>
    /// <param name="options">The roots, probe settings and bounds.</param>
    /// <param name="cancellationToken">Cancels discovery rather than returning a partial success.</param>
    /// <returns>Borrowed daemon handles, diagnostics and truncation information.</returns>
    /// <remarks>
    /// Scans immediate directory children, skips symlink roots and entries, and probes only sockets owned
    /// by the current Unix user. Duplicate daemon generations yield one handle and a diagnostic.
    /// Root components resolve through the filesystem before enumeration; missing components are errors.
    /// No-start probes cannot create a daemon. Filesystem calls themselves are synchronous and may exceed
    /// the deadline on an unresponsive filesystem; subsequent work stops at the next boundary.
    /// Cancellation during a probe retains the caller's token and the original dispatch diagnostics.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    public static async Task<ServerDiscoveryResult> DiscoverAsync(
        ServerDiscoveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ServerDiscoveryOptions settings = options ?? new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.MaximumRoots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.MaximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.MaximumProbes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(settings.Timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(settings.ProbeTimeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(settings.Roots);
        TmuxConnection template = Open(settings.Connection).Connection!;
        List<string> roots = [.. settings.Roots.Take(settings.MaximumRoots == int.MaxValue ? int.MaxValue : settings.MaximumRoots + 1)];
        if (settings.IncludeConfiguredRoots)
        {
            string suffix = "tmux-" + UnixSocketDirectory.UserId.ToString(CultureInfo.InvariantCulture);
            string configured = template.ChildEnvironment.TryGetValue("TMUX_TMPDIR", out string? value)
                && !string.IsNullOrEmpty(value) ? value : "/tmp";
            roots.Add(Path.Combine(configured, suffix));
            roots.Add(Path.GetDirectoryName(template.SocketPath)!);
        }
        List<DiscoveredServer> servers = [];
        List<ServerDiscoveryDiagnostic> diagnostics = [];
        HashSet<ServerGeneration> generations = [];
        HashSet<string> seenPaths = new(StringComparer.Ordinal);
        HashSet<string> seenRoots = new(StringComparer.Ordinal);
        int entries = 0;
        int probes = 0;
        int rootCount = 0;
        bool truncated = false;
        Stopwatch elapsed = Stopwatch.StartNew();
        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rootCount >= settings.MaximumRoots || elapsed.Elapsed >= settings.Timeout)
            {
                Stop(root, "The root or total time bound was reached.");
                break;
            }
            rootCount++;
            if (!seenRoots.Add(root))
            {
                diagnostics.Add(new(root, "duplicate", "The root was already inspected."));
                continue;
            }
            try
            {
                TmuxConnectionEndpoint.ValidateSocketPath(root, nameof(settings.Roots));
                string? resolvedRoot = UnixSocketDirectory.ResolveDiscoveryRoot(root);
                if (resolvedRoot is null)
                {
                    diagnostics.Add(new(root, "skipped", "Symbolic-link roots are not followed."));
                    continue;
                }
                foreach (string entry in Directory.EnumerateFileSystemEntries(resolvedRoot))
                {
                    string candidate = Path.Combine(root, Path.GetFileName(entry));
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entries >= settings.MaximumEntries || elapsed.Elapsed >= settings.Timeout)
                    {
                        Stop(candidate, "The entry or total time bound was reached.");
                        break;
                    }
                    entries++;
                    try
                    {
                        if (!UnixSocketDirectory.IsOwnedSocket(candidate))
                        {
                            diagnostics.Add(new(candidate, "skipped", "The entry is not a socket owned by the current user; symbolic links are not followed."));
                            continue;
                        }
                        if (!seenPaths.Add(candidate))
                        {
                            diagnostics.Add(new(candidate, "duplicate", "The socket path was already probed."));
                            continue;
                        }
                        if (probes >= settings.MaximumProbes)
                        {
                            Stop(candidate, "The probe bound was reached.");
                            break;
                        }
                        probes++;
                        TimeSpan budget = settings.Timeout - elapsed.Elapsed;
                        if (budget <= TimeSpan.Zero)
                        {
                            Stop(candidate, "The total time bound was reached.");
                            break;
                        }
                        if (budget > settings.ProbeTimeout)
                        {
                            budget = settings.ProbeTimeout;
                        }
                        Server endpoint = new(template.AtSocketPath(candidate, budget), null, null);
                        Server? daemon = await endpoint.InspectAsync(cancellationToken).ConfigureAwait(false);
                        if (daemon is null)
                        {
                            diagnostics.Add(new(candidate, "absent", "No daemon is listening at this socket."));
                        }
                        else if (generations.Add(daemon.Generation!.Value))
                        {
                            servers.Add(new(candidate, daemon));
                        }
                        else
                        {
                            diagnostics.Add(new(candidate, "duplicate", "This daemon generation was already found through another path."));
                        }
                    }
                    catch (OperationCanceledException failure)
                        when (cancellationToken.IsCancellationRequested && failure.CancellationToken != cancellationToken)
                    {
                        throw failure is TmuxOperationCanceledException dispatched
                            ? new TmuxOperationCanceledException(
                                dispatched.Message,
                                cancellationToken,
                                dispatched.CommandMayHaveExecuted,
                                dispatched.ClientProcessId,
                                dispatched)
                            : new OperationCanceledException(failure.Message, failure, cancellationToken);
                    }
                    catch (Exception failure) when (failure is not OperationCanceledException)
                    {
                        diagnostics.Add(new(candidate, "probe-error", failure.Message));
                    }
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
            {
                diagnostics.Add(new(root, "root-error", failure.Message));
            }
            if (truncated)
            {
                break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ServerDiscoveryResult(servers, diagnostics, truncated, entries, probes);

        void Stop(string path, string message)
        {
            truncated = true;
            diagnostics.Add(new(path, "limit", message));
        }
    }
}
