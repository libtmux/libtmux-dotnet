using System.Collections.Immutable;
using System.Runtime.Versioning;
using ModelContextProtocol;

namespace LibTmux.Mcp;

[UnsupportedOSPlatform("windows")]
internal sealed record McpStartup(
    ServerConnectionOptions ConnectionOptions,
    CapabilitySelection Selection,
    McpRuntimeDisclosure Disclosure)
{
    internal const string SocketVariable = "LIBTMUX_SOCKET";
    internal const string SocketPathVariable = "LIBTMUX_SOCKET_PATH";

    /// <summary>The variable the library read for a socket name before it read <c>LIBTMUX_SOCKET</c>.</summary>
    internal const string LibrarySocketNameVariable = "LIBTMUX_SOCKET_NAME";
    internal const string ConfigurationVariable = "LIBTMUX_TMUX_CONFIG";
    private const string TmuxBinaryVariable = "LIBTMUX_TMUX";
    private const string TmuxTemporaryDirectoryVariable = "TMUX_TMPDIR";
    private const string TmuxServerVariable = "TMUX";
    private const string PathVariable = "PATH";

    internal static string MinimalConfigurationPath =>
        Path.Combine(AppContext.BaseDirectory, "minimal.conf");

    internal static async Task<McpStartup> ResolveAsync(
        Func<string, string?> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        string[] variables =
        [
            ServerPolicy.SafetyVariable,
            CapabilitySelection.ToolsetsVariable,
            CapabilitySelection.ToolsVariable,
            CapabilitySelection.ExcludeToolsVariable,
            SocketVariable,
            SocketPathVariable,
            LibrarySocketNameVariable,
            ConfigurationVariable,
            TmuxBinaryVariable,
            TmuxTemporaryDirectoryVariable,
            TmuxServerVariable,
            PathVariable,
        ];
        Dictionary<string, string?> environment = variables.ToDictionary(
            name => name,
            read,
            StringComparer.Ordinal);
        string? Read(string name) => environment[name];

        // Validate retired settings, toolset tokens, and tool names before the
        // first tmux process is opened.
        CapabilitySelection parsed = CapabilitySelection.FromEnvironment(
            Read,
            ImmutableHashSet<Toolset>.Empty);

        string? socketName = NonBlank(Read(SocketVariable), SocketVariable);
        string? socketPath = NonBlank(Read(SocketPathVariable), SocketPathVariable);
        RequireSafeRouteValue(socketName, SocketVariable);
        RequireSafeRouteValue(socketPath, SocketPathVariable);

        // With both set the path wins, as it does in libtmux-mcp, which hands
        // both to Server and gets the one socket_path names.
        if (socketPath is not null)
        {
            socketName = null;
        }

        // Neither socket variable leaves the choice to tmux: the server in
        // TMUX, otherwise its default socket. The name the library read before
        // LIBTMUX_SOCKET selects nothing now, so setting only that one would
        // land on that server while the caller believes it is somewhere else.
        // Socket selection is frozen at startup and the capability model treats
        // it as a boundary, so refusing beats ignoring: a probe that thought it
        // was isolated read and wrote another server's sessions for hours.
        if (socketName is null
            && socketPath is null
            && NonBlank(Read(LibrarySocketNameVariable), LibrarySocketNameVariable) is not null)
        {
            throw new McpException(
                $"{LibrarySocketNameVariable} no longer selects a socket. "
                + $"Use {SocketVariable} for a socket name, or "
                + $"{SocketPathVariable} for an absolute path.");
        }

        bool unselected = socketName is null && socketPath is null;
        if (socketPath is not null && !Path.IsPathFullyQualified(socketPath))
        {
            throw new McpException($"{SocketPathVariable} must be a fully qualified path.");
        }

        (string? configurationFile, string configurationProvenance) =
            ParseConfiguration(Read(ConfigurationVariable), unselected);
        string binary = ResolveExecutablePath(
            NonBlank(Read(TmuxBinaryVariable), TmuxBinaryVariable) ?? "tmux",
            Read(PathVariable));
        string? tmuxTemporaryDirectory = Read(TmuxTemporaryDirectoryVariable);
        RequireSafeRouteValue(
            tmuxTemporaryDirectory,
            TmuxTemporaryDirectoryVariable);
        string? tmuxServer = Read(TmuxServerVariable);
        if (unselected)
        {
            RequireSafeRouteValue(tmuxServer, TmuxServerVariable);
        }

        Dictionary<string, string?> childEnvironment = ChildEnvironment(
            tmuxTemporaryDirectory,
            unselected ? tmuxServer : null,
            unselected);
        ServerConnectionOptions options = Options(
            binary,
            socketName,
            socketPath,
            configurationFile,
            childEnvironment);
        ProbeState probe = await ProbeAsync(Server.Open(options), cancellationToken)
            .ConfigureAwait(false);
        bool existing = probe == ProbeState.Existing;

        // Only a server the caller started or selected is ever driven here, so
        // teardown is never a default.
        ImmutableHashSet<Toolset> defaults =
            ImmutableHashSet.Create(Toolset.Inspect, Toolset.Manage, Toolset.Execute);
        CapabilitySelection selection = Read(CapabilitySelection.ToolsetsVariable) is null
            ? parsed with { Toolsets = defaults }
            : parsed;

        bool teardownWasNamed =
            (Read(CapabilitySelection.ToolsetsVariable) is not null
                && selection.Toolsets.Contains(Toolset.Teardown))
            || selection.IncludedNames.Any(name => CapabilityRegistry.Manifest.Any(
                definition => definition.Name == name && definition.Toolset == Toolset.Teardown));
        bool explicitlySelectedTeardown = teardownWasNamed
            && CapabilityRegistry.Manifest.Any(definition =>
                definition.Toolset == Toolset.Teardown && selection.Includes(definition));
        string selector = unselected
            ? McpRuntimeDisclosure.DefaultSelector
            : socketPath is null
                ? $"name:{socketName}"
                : $"path:{socketPath}";
        string socketProvenance = unselected ? "tmux-default" : "operator-current";
        string reportedConfiguration = ReportedConfigurationProvenance(
            configurationProvenance,
            existing);
        string serverState = existing ? "existing" : "absent";
        string resolvedSocketPath = socketPath is null ? "" : Path.GetFullPath(socketPath);
        if (existing)
        {
            resolvedSocketPath = ComposeSocketPath(socketName, childEnvironment)
                ?? await ResolveSocketPathAsync(Server.Open(options), cancellationToken)
                    .ConfigureAwait(false);
        }
        ServerConnectionOptions pinnedOptions = resolvedSocketPath.Length == 0
            ? options
            : Options(
                binary,
                socketName: null,
                socketPath: resolvedSocketPath,
                configurationFile: configurationFile,
                childEnvironment: ChildEnvironment(
                    tmuxTemporaryDirectory,
                    tmuxServer: null,
                    unselected: false));
        return new McpStartup(
            pinnedOptions,
            selection,
            new McpRuntimeDisclosure(
                selector,
                socketProvenance,
                reportedConfiguration,
                serverState,
                resolvedSocketPath,
                McpRuntimeDisclosure.BuildAttachCommand(pinnedOptions, resolvedSocketPath),
                explicitlySelectedTeardown));
    }

    /// <summary>Builds the path a socket name resolves to, without asking tmux.</summary>
    /// <param name="socketName">The pinned socket name, if the pin is a name.</param>
    /// <param name="childEnvironment">The environment tmux was given.</param>
    /// <returns>The path, or <see langword="null" /> to ask tmux instead.</returns>
    /// <remarks>
    /// Asking tmux does not round-trip. tmux escapes a non-printable byte in
    /// the socket path when it stores it at server start, so on 3.4 and 3.5 a
    /// socket named with one reports back as printable escapes naming no file
    /// -- and that path is what a run passes to <c>-S</c> and what pane input
    /// stats. tmux composes the path itself as
    /// <c>&lt;root&gt;/tmux-&lt;uid&gt;/&lt;name&gt;</c> from the environment
    /// it was started with, which is the environment this process froze, so it
    /// can be built here byte for byte. Answering null falls back to asking,
    /// which is right for a server this process did not name.
    /// </remarks>
    private static string? ComposeSocketPath(
        string? socketName,
        Dictionary<string, string?> childEnvironment)
    {
        if (socketName is null || OperatingSystem.IsWindows())
        {
            return null;
        }

        string root =
            childEnvironment.TryGetValue(TmuxTemporaryDirectoryVariable, out string? configured)
            && !string.IsNullOrWhiteSpace(configured)
                ? configured
                : "/tmp";
        string composed = Path.Combine(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            $"tmux-{PaneInputEndpoint.UserId}",
            socketName);
        return File.Exists(composed) ? composed : null;
    }

    private static async Task<string> ResolveSocketPathAsync(
        Server server,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result = await server.ExecuteCommandAsync(
                ["display-message", "-p", "#{socket_path}"],
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || result.StandardOutputLines.Count != 1
            || string.IsNullOrWhiteSpace(result.StandardOutputLines[0]))
        {
            throw new McpException("Could not resolve the selected tmux socket path.");
        }

        string path = result.StandardOutputLines[0];
        RequireSafeRouteValue(path, "resolved tmux socket path");
        if (!Path.IsPathFullyQualified(path))
        {
            throw new McpException(
                "The selected tmux server returned a non-absolute socket path.");
        }

        return path;
    }

    private static (string? Path, string Provenance) ParseConfiguration(string? value, bool unselected)
    {
        if (value is null)
        {
            // The bundled configuration keeps a server of this server's own
            // alive while it has no sessions. It is not for the server tmux
            // picks, which belongs to the person using it and loads their own.
            if (unselected)
            {
                return (null, "tmux-default");
            }

            if (!File.Exists(MinimalConfigurationPath))
            {
                throw new McpException(
                    $"The bundled minimal tmux configuration is missing: {MinimalConfigurationPath}.");
            }

            return (MinimalConfigurationPath, "minimal");
        }

        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new McpException(
                $"{ConfigurationVariable} must be a nonempty absolute path.");
        }

        return (value, "user-configured");
    }

    internal static string ReportedConfigurationProvenance(string configured, bool existing) =>
        existing ? "unknown" : configured;

    private static ServerConnectionOptions Options(
        string binary,
        string? socketName,
        string? socketPath,
        string? configurationFile,
        IReadOnlyDictionary<string, string?> childEnvironment) => new()
        {
            TmuxBinaryPath = binary,
            SocketName = socketName,
            SocketPath = socketPath,
            ConfigurationFile = configurationFile,
            ChildEnvironment = childEnvironment,
        };

    private static Dictionary<string, string?> ChildEnvironment(
        string? tmuxTemporaryDirectory,
        string? tmuxServer,
        bool unselected)
    {
        string? frozen = string.IsNullOrWhiteSpace(tmuxTemporaryDirectory)
            ? null
            : Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(tmuxTemporaryDirectory));
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [TmuxTemporaryDirectoryVariable] = frozen,
        };

        if (unselected)
        {
            // With no -L and no -S, tmux follows TMUX and then its default
            // socket. The values this server read are the ones it hands on, so
            // the library does not read the process again and find others.
            environment[TmuxServerVariable] = tmuxServer;
            environment[SocketVariable] = null;
            environment[SocketPathVariable] = null;
        }

        return environment;
    }

    internal static string ResolveExecutablePath(string binary, string? searchPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binary);
        RequireSafeRouteValue(binary, TmuxBinaryVariable);
        RequireSafeRouteValue(searchPath, PathVariable);

        IEnumerable<string> candidates = binary.Contains(Path.DirectorySeparatorChar)
            || binary.Contains(Path.AltDirectorySeparatorChar)
                ? [binary]
                : (searchPath ?? string.Empty)
                    .Split(Path.PathSeparator)
                    .Select(directory => Path.Combine(
                        directory.Length == 0 ? Environment.CurrentDirectory : directory,
                        binary));
        foreach (string candidate in candidates)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
                if (IsExecutableFile(fullPath))
                {
                    return fullPath;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        throw new McpException(
            $"{TmuxBinaryVariable} did not resolve to an executable file at startup.");
    }

    internal static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode executable = UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            return (mode & executable) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<ProbeState> ProbeAsync(
        Server server,
        CancellationToken cancellationToken)
    {
        TmuxCommandResult result;
        try
        {
            result = await server.ExecuteCommandAsync(["list-sessions"], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TmuxVersionTooLowException error)
        {
            // The refusal already says which version it found and what to do.
            throw new McpException(ToolFailureFilter.RefusalMessage(error), error);
        }
        catch (TmuxProtocolException error) when (error.Dispatch is TmuxDispatchState.NotDispatched)
        {
            // The version probe failed before any command went out, and its
            // message says why.
            throw new McpException(
                $"Could not establish tmux daemon provenance: {error.Message}",
                error);
        }
        catch (LibTmuxException error)
        {
            throw new McpException("Could not establish tmux daemon provenance.", error);
        }

        if (result.ExitCode == 0)
        {
            return ProbeState.Existing;
        }

        string errorText = string.Join('\n', result.StandardErrorLines);
        if (errorText.Contains("no server running", StringComparison.Ordinal)
            || errorText.Contains("No such file or directory", StringComparison.Ordinal))
        {
            return ProbeState.Absent;
        }

        throw new McpException(
            $"Could not establish tmux daemon provenance: {errorText.Trim()}".TrimEnd());
    }

    private static string? NonBlank(string? value, string variable)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new McpException($"{variable} cannot be empty.");
        }

        return value;
    }

    internal static void RequireSafeRouteValue(string? value, string variable)
    {
        if (value is null)
        {
            return;
        }

        foreach (char character in value)
        {
            if (character <= '\u001f' || character == '\u007f')
            {
                throw new McpException(
                    $"{variable} must not contain an ASCII control character or DEL.");
            }
        }
    }

    private enum ProbeState
    {
        Absent,
        Existing,
    }
}
