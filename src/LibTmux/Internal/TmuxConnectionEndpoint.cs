using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LibTmux.Internal;

/// <summary>Resolves one immutable connection endpoint and its child environment.</summary>
internal static class TmuxConnectionEndpoint
{
    private const string DefaultSocketRoot = "/tmp";
    private const string SocketNameVariable = "LIBTMUX_SOCKET_NAME";
    private const string SocketPathVariable = "LIBTMUX_SOCKET_PATH";

    internal static ResolvedTmuxConnection Resolve(ServerConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        bool chosen = options.SocketPath is not null
            || options.SocketName is not null
            || options.SocketNameFactory is not null;
        string? environmentSocketName = chosen
            ? null
            : ReadVariable(options.ChildEnvironment, SocketNameVariable);

        string? socketPath = options.SocketPath is not null
            ? Path.GetFullPath(options.SocketPath)
            : NormalizeSocketPath(
                chosen ? null : ReadVariable(options.ChildEnvironment, SocketPathVariable));
        string? socketName = null;
        IReadOnlyDictionary<string, string?>? childEnvironment = options.ChildEnvironment;
        TmuxEndpointIdentity endpointIdentity;
        if (socketPath is null)
        {
            socketName = options.SocketName;
            if (socketName is null && options.SocketNameFactory is not null)
            {
                socketName = options.SocketNameFactory();
                if (string.IsNullOrWhiteSpace(socketName))
                {
                    throw new InvalidOperationException(
                        "The selected socket-name factory returned no usable name.");
                }
            }

            socketName ??= environmentSocketName;
            socketName ??= "default";
            ResolvedSocketRoot socketRoot = ResolveSocketRoot(options.ChildEnvironment);
            childEnvironment = FreezeChildEnvironment(
                options.ChildEnvironment,
                socketRoot.EnvironmentValue,
                options.PsmuxPreview?.DataDirectory);
            endpointIdentity = options.PsmuxPreview is null
                ? TmuxEndpointIdentity.ForName(socketRoot.Identity, socketName)
                : TmuxEndpointIdentity.ForPsmux(options.PsmuxPreview.DataDirectory, socketName);
        }
        else
        {
            endpointIdentity = TmuxEndpointIdentity.ForPath(socketPath);
        }

        return new ResolvedTmuxConnection(
            options,
            BuildPrefixArguments(options, socketPath, socketName),
            socketName,
            socketPath,
            endpointIdentity,
            childEnvironment);
    }

    private static string[] BuildPrefixArguments(
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
        string? value;
        if (childEnvironment is null || !childEnvironment.TryGetValue(name, out value))
        {
            value = Environment.GetEnvironmentVariable(name);
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? NormalizeSocketPath(string? socketPath) =>
        socketPath is null ? null : Path.GetFullPath(socketPath);

    private static ResolvedSocketRoot ResolveSocketRoot(
        IReadOnlyDictionary<string, string?>? childEnvironment)
    {
        string? configuredRoot = ReadVariable(childEnvironment, "TMUX_TMPDIR");
        if (string.IsNullOrEmpty(configuredRoot))
        {
            return new ResolvedSocketRoot(
                NormalizeSocketRoot(DefaultSocketRoot),
                EnvironmentValue: null);
        }

        string normalizedRoot = NormalizeSocketRoot(configuredRoot);
        return new ResolvedSocketRoot(normalizedRoot, normalizedRoot);
    }

    private static ReadOnlyDictionary<string, string?> FreezeChildEnvironment(
        IReadOnlyDictionary<string, string?>? childEnvironment,
        string? socketRoot,
        string? psmuxDataDirectory)
    {
        var frozen = childEnvironment is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(childEnvironment, StringComparer.Ordinal);
        frozen["TMUX_TMPDIR"] = socketRoot;
        if (psmuxDataDirectory is not null)
        {
            frozen["PSMUX_DATA_DIR"] = psmuxDataDirectory;
        }

        return new ReadOnlyDictionary<string, string?>(frozen);
    }

    private static string NormalizeSocketRoot(string socketRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(socketRoot));

    private readonly record struct ResolvedSocketRoot(
        string Identity,
        string? EnvironmentValue);
}

internal sealed record ResolvedTmuxConnection(
    ServerConnectionOptions Options,
    string[] PrefixArguments,
    string? SocketName,
    string? SocketPath,
    TmuxEndpointIdentity EndpointIdentity,
    IReadOnlyDictionary<string, string?>? ChildEnvironment);

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
