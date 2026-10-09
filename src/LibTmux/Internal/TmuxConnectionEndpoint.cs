using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LibTmux.Internal;

/// <summary>Resolves one immutable connection endpoint and its child environment.</summary>
internal static class TmuxConnectionEndpoint
{
    /// <summary>The socket tmux uses when a caller names none.</summary>
    internal const string DefaultSocketName = "default";

    private const string DefaultSocketRoot = "/tmp";
    private const string SocketNameVariable = "LIBTMUX_SOCKET_NAME";
    private const string SocketPathVariable = "LIBTMUX_SOCKET_PATH";

    internal static ResolvedTmuxConnection Resolve(ServerConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Dictionary<string, string?> environment = CaptureEnvironment(options.ChildEnvironment);

        if (options.SocketPath is not null
            && (options.SocketName is not null || options.SocketNameFactory is not null))
        {
            throw new ArgumentException(
                "A connection cannot specify both a socket path and a socket name.", nameof(options));
        }

        bool chosen = options.SocketPath is not null
            || options.SocketName is not null
            || options.SocketNameFactory is not null;
        string? socketPath = options.SocketPath;
        string? socketName = options.SocketName;
        if (socketName is null && options.SocketNameFactory is not null)
        {
            socketName = options.SocketNameFactory()
                ?? throw new ArgumentException("The socket-name factory returned null.", nameof(options));
        }

        if (!chosen)
        {
            socketPath = ReadVariable(environment, SocketPathVariable);
            if (socketPath is null)
            {
                socketName = ReadVariable(environment, SocketNameVariable);
                if (socketName is null
                    && ReadVariable(environment, "TMUX") is string tmux)
                {
                    if (ReadRawVariable(environment, "PSMUX_SESSION") is not null
                        || tmux.StartsWith("/tmp/psmux-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "A psmux context requires explicit PsmuxConnectionOptions.", nameof(options));
                    }

                    if (!TmuxEnvironmentVariables.TryParse(tmux, out TmuxServerLocation? location))
                    {
                        throw new ArgumentException($"The selected TMUX value '{tmux}' is invalid.", nameof(options));
                    }

                    socketPath = location.SocketPath;
                }
            }
        }

        string? socketDirectory = null;
        string? socketRoot = null;
        TmuxEndpointIdentity endpointIdentity;
        string? launchPath = socketPath;
        if (socketPath is not null)
        {
            ValidateSocketPath(socketPath, nameof(options.SocketPath));
            endpointIdentity = TmuxEndpointIdentity.ForPath(socketPath);
        }
        else
        {
            socketName ??= DefaultSocketName;
            ValidateSocketName(socketName, nameof(options.SocketName));
            if (options.PsmuxPreview is { } psmux)
            {
                PsmuxCompatibility.ValidateNamespaceName(socketName, nameof(options.SocketName));
                endpointIdentity = TmuxEndpointIdentity.ForPsmux(psmux.DataDirectory, socketName);
            }
            else
            {
                socketRoot = ReadVariable(environment, "TMUX_TMPDIR") ?? DefaultSocketRoot;
                ValidateSocketPath(socketRoot, "TMUX_TMPDIR");
                endpointIdentity = TmuxEndpointIdentity.ForName(socketRoot, socketName);
                if (!OperatingSystem.IsWindows())
                {
                    socketDirectory = Path.Combine(socketRoot,
                        $"tmux-{UnixSocketDirectory.UserId.ToString(CultureInfo.InvariantCulture)}");
                    launchPath = Path.Combine(socketDirectory, socketName);
                }
            }
        }

        string? executablePath = CaptureExecutable(options.TmuxBinaryPath, environment);
        var frozen = new Dictionary<string, string?>(environment, environment.Comparer);
        foreach (string name in frozen.Keys.Where(name => name.StartsWith("PSMUX_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (options.ChildEnvironment is null || !options.ChildEnvironment.ContainsKey(name))
            {
                frozen.Remove(name);
            }
        }
        if (socketRoot is not null)
        {
            frozen["TMUX_TMPDIR"] = socketRoot;
        }

        if (options.PsmuxPreview is { } preview)
        {
            frozen["PSMUX_DATA_DIR"] = preview.DataDirectory;
        }

        frozen["TMUX"] = null;
        frozen["TMUX_PANE"] = null;
        return new ResolvedTmuxConnection(
            options,
            BuildPrefixArguments(options, launchPath, socketName),
            socketName,
            socketPath,
            endpointIdentity,
            new ReadOnlyDictionary<string, string?>(frozen),
            socketDirectory,
            executablePath);
    }

    private static Dictionary<string, string?> CaptureEnvironment(IReadOnlyDictionary<string, string?>? overrides)
    {
        var environment = new Dictionary<string, string?>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment.Add((string)entry.Key, (string?)entry.Value);
        }

        foreach ((string key, string? value) in overrides ?? new Dictionary<string, string?>())
        {
            environment[key] = value;
        }

        return environment;
    }

    private static string? CaptureExecutable(string binary, IReadOnlyDictionary<string, string?> environment)
    {
        if (Path.IsPathFullyQualified(binary))
        {
            return binary;
        }

        string directory = Environment.CurrentDirectory;
        if (binary.Contains(Path.DirectorySeparatorChar) || binary.Contains(Path.AltDirectorySeparatorChar))
        {
            return Path.Join(directory, binary);
        }

        if (ReadRawVariable(environment, "PATH") is not string path)
        {
            return null;
        }

        foreach (string entry in path.Split(Path.PathSeparator))
        {
            string root = Path.IsPathFullyQualified(entry) ? entry : Path.Join(directory, entry);
            string candidate = Path.Join(root, binary);
            if (OperatingSystem.IsWindows() && !Path.HasExtension(candidate))
            {
                candidate += ".exe";
            }

            if (File.Exists(candidate)
                && (OperatingSystem.IsWindows()
                    || (File.GetUnixFileMode(candidate)
                        & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static void ValidateSocketPath(string path, string parameterName)
    {
        if (path.Length == 0 || path.Contains('\0') || !IsAbsoluteSocketPath(path))
        {
            throw new ArgumentException($"Socket path '{path}' must be absolute and contain no NUL.", parameterName);
        }
    }

    internal static bool IsAbsoluteSocketPath(string path) =>
        path.StartsWith('/') || Path.IsPathFullyQualified(path);

    internal static void ValidateSocketName(string name, string parameterName)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new ArgumentException($"Socket name '{name}' must be a nonempty leaf name.", parameterName);
        }
    }

    internal static string[] BuildPrefixArguments(
        ServerConnectionOptions options,
        string? socketPath,
        string? socketName)
    {
        var arguments = new List<string>();
        if (options.PsmuxPreview is null)
        {
            // Without this, tmux replaces tabs and Unicode under the C locale.
            arguments.Add("-u");
        }
        switch (options.ColorMode)
        {
            case TmuxColorMode.Default:
                break;
            case TmuxColorMode.Colors256:
                arguments.Add("-2");
                break;
            case TmuxColorMode.TrueColor:
                arguments.Add("-T");
                arguments.Add("RGB");
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.ColorMode,
                    "The tmux color mode is not defined.");
        }

        if (options.ConfigurationFile is not null)
        {
            arguments.Add("-f");
            arguments.Add(options.ConfigurationFile);
        }

        if (socketPath is not null)
        {
            arguments.Add("-S");
            arguments.Add(socketPath);
        }
        else if (socketName is not null)
        {
            arguments.Add("-L");
            arguments.Add(socketName);
        }

        return [.. arguments];
    }

    private static string? ReadVariable(
        IReadOnlyDictionary<string, string?>? childEnvironment,
        string name)
    {
        string? value = ReadRawVariable(childEnvironment, name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? ReadRawVariable(
        IReadOnlyDictionary<string, string?>? childEnvironment,
        string name) =>
        childEnvironment is not null && childEnvironment.TryGetValue(name, out string? value)
            ? value
            : null;
}

internal sealed record ResolvedTmuxConnection(
    ServerConnectionOptions Options,
    string[] PrefixArguments,
    string? SocketName,
    string? SocketPath,
    TmuxEndpointIdentity EndpointIdentity,
    IReadOnlyDictionary<string, string?> ChildEnvironment,
    string? SocketDirectory,
    string? ExecutablePath);

internal readonly record struct TmuxEndpointIdentity(
    TmuxEndpointKind Kind,
    string Primary,
    string? Secondary)
{
    internal static TmuxEndpointIdentity ForPath(string socketPath) =>
        new(TmuxEndpointKind.Path, socketPath, Secondary: null);

    internal static TmuxEndpointIdentity ForName(string socketRoot, string socketName) =>
        new(TmuxEndpointKind.Name, socketRoot, socketName);

    internal static TmuxEndpointIdentity ForPsmux(string dataDirectory, string socketName) =>
        new(TmuxEndpointKind.Psmux, dataDirectory, socketName);

    internal string Fingerprint()
    {
        string material = string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)Kind}:{Primary.Length}:{Primary}:"
            + $"{(Secondary is null ? -1 : Secondary.Length)}:{Secondary}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }
}

internal enum TmuxEndpointKind
{
    Path,
    Name,
    Psmux,
}
