using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
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
    internal const string ConfigurationVariable = "LIBTMUX_TMUX_CONFIG";
    private const string TmuxBinaryVariable = "LIBTMUX_TMUX";
    private const string TmuxTemporaryDirectoryVariable = "TMUX_TMPDIR";
    private const string OwnerVariable = "LIBTMUX_MCP_OWNER";
    private const string OwnerOption = "@libtmux_mcp_owner";
    private const string DedicatedSocketName = "libtmux-mcp";

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
            ConfigurationVariable,
            TmuxBinaryVariable,
            TmuxTemporaryDirectoryVariable,
        ];
        Dictionary<string, string?> environment = variables.ToDictionary(
            name => name,
            read,
            StringComparer.Ordinal);
        string? Read(string name) => environment[name];

        // Validate retired settings, toolset tokens, and tool names before the
        // first tmux process is opened. The default subset is filled only after
        // the liveness probe establishes socket provenance.
        CapabilitySelection parsed = CapabilitySelection.FromEnvironment(
            Read,
            ImmutableHashSet<Toolset>.Empty);

        string? socketName = NonBlank(Read(SocketVariable), SocketVariable);
        string? socketPath = NonBlank(Read(SocketPathVariable), SocketPathVariable);
        if (socketName is not null && socketPath is not null)
        {
            throw new McpException(
                $"{SocketVariable} and {SocketPathVariable} are mutually exclusive.");
        }

        bool dedicated = socketName is null && socketPath is null;
        socketName ??= dedicated ? DedicatedSocketName : null;
        if (socketPath is not null && !Path.IsPathFullyQualified(socketPath))
        {
            throw new McpException($"{SocketPathVariable} must be a fully qualified path.");
        }

        (string? configurationFile, string configurationProvenance) =
            ParseConfiguration(Read(ConfigurationVariable));
        string binary = NonBlank(Read(TmuxBinaryVariable), TmuxBinaryVariable) ?? "tmux";
        Dictionary<string, string?> childEnvironment = ChildEnvironment(
            Read(TmuxTemporaryDirectoryVariable));
        ServerConnectionOptions options = Options(
            binary,
            socketName,
            socketPath,
            configurationFile,
            childEnvironment);
        ProbeState probe = await ProbeAsync(Server.Open(options), cancellationToken)
            .ConfigureAwait(false);
        bool existing = probe == ProbeState.Existing;
        bool newDedicatedMinimal = false;
        if (dedicated && !existing
            && string.Equals(configurationProvenance, "minimal", StringComparison.Ordinal))
        {
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                .ToLowerInvariant();
            Dictionary<string, string?> startupEnvironment = new(
                childEnvironment,
                StringComparer.Ordinal)
            {
                [OwnerVariable] = nonce,
            };
            Server starting = Server.Open(Options(
                binary,
                socketName,
                socketPath,
                configurationFile,
                startupEnvironment));
            await starting.StartServerAsync(cancellationToken).ConfigureAwait(false);
            TmuxCommandResult marker = await starting.ExecuteCommandAsync(
                    ["show-options", "-gqv", OwnerOption],
                    cancellationToken)
                .ConfigureAwait(false);
            if (marker.ExitCode != 0
                || !string.Equals(
                    string.Join('\n', marker.StandardOutputLines).TrimEnd(),
                    nonce,
                    StringComparison.Ordinal))
            {
                throw new McpException(
                    "Another tmux daemon won startup; provenance is unknown.");
            }

            newDedicatedMinimal = true;
        }
        ImmutableHashSet<Toolset> defaults = newDedicatedMinimal
            ? Enum.GetValues<Toolset>().ToImmutableHashSet()
            : ImmutableHashSet.Create(Toolset.Inspect, Toolset.Manage, Toolset.Execute);
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
        string selector = socketPath is null
            ? $"name:{socketName}"
            : $"path:{socketPath}";
        string socketProvenance = dedicated ? "default-dedicated" : "operator-current";
        string reportedConfiguration = ReportedConfigurationProvenance(
            configurationProvenance,
            existing);
        string serverState = existing
            ? "existing"
            : newDedicatedMinimal ? "created" : "absent";
        return new McpStartup(
            options,
            selection,
            new McpRuntimeDisclosure(
                selector,
                socketProvenance,
                reportedConfiguration,
                serverState,
                explicitlySelectedTeardown));
    }

    private static (string? Path, string Provenance) ParseConfiguration(string? value)
    {
        if (value is null)
        {
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
        string.Equals(configured, "user-configured", StringComparison.Ordinal)
            ? "user-configured"
            : existing ? "unknown" : "minimal";

    private static ServerConnectionOptions Options(
        string binary,
        string? socketName,
        string? socketPath,
        string? configurationFile,
        IReadOnlyDictionary<string, string?> childEnvironment) => new(
            tmuxBinaryPath: binary,
            socketName: socketName,
            socketPath: socketPath,
            configurationFile: configurationFile,
            childEnvironment: childEnvironment);

    private static Dictionary<string, string?> ChildEnvironment(string? tmuxTemporaryDirectory)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (tmuxTemporaryDirectory is not null)
        {
            environment[TmuxTemporaryDirectoryVariable] = tmuxTemporaryDirectory;
        }

        return environment;
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

    private enum ProbeState
    {
        Absent,
        Existing,
    }
}
