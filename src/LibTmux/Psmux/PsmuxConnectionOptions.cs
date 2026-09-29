using LibTmux.Internal;

namespace LibTmux;

/// <summary>Configures the bounded psmux query preview.</summary>
/// <remarks>
/// The executable is verified before every client launch. The already-running
/// psmux server must have been provisioned separately with the same clean build,
/// data directory, namespace, and an alias-free configuration.
/// </remarks>
public sealed class PsmuxConnectionOptions
{
    /// <summary>Initializes one explicit psmux endpoint.</summary>
    /// <param name="executablePath">
    /// The fully qualified <c>psmux.exe</c> path on a fixed local Windows drive.
    /// Use its <c>/mnt/...</c> path when WSL launches psmux.
    /// </param>
    /// <param name="expectedBinarySha256">
    /// The exact audited client SHA-256 exposed by
    /// <see cref="PsmuxServer.SupportedBinarySha256" />.
    /// </param>
    /// <param name="dataDirectory">
    /// A canonical, absolute data-directory path on a fixed local Windows drive.
    /// </param>
    /// <param name="namespaceName">
    /// A dedicated non-default <c>-L</c> namespace containing exactly one session.
    /// </param>
    /// <param name="logSink">The optional sink for the library's diagnostics.</param>
    /// <exception cref="ArgumentException">
    /// A path, hash, or namespace is absent, malformed, ambiguous, not fixed-drive,
    /// or not isolated.
    /// </exception>
    public PsmuxConnectionOptions(
        string executablePath,
        string expectedBinarySha256,
        string dataDirectory,
        string namespaceName,
        Action<TmuxLogEntry>? logSink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.IndexOfAny(['\0', '\r', '\n']) >= 0
            || !Path.IsPathFullyQualified(executablePath)
            || executablePath.StartsWith("\\\\", StringComparison.Ordinal)
            || executablePath.StartsWith("//", StringComparison.Ordinal)
            || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The psmux executable must be a local, fully qualified .exe path without control characters.",
                nameof(executablePath));
        }

        ExecutablePath = Path.GetFullPath(executablePath);
        PsmuxCompatibility.EnsureNativeFixedDrive(ExecutablePath, nameof(executablePath));
        ExpectedBinarySha256 = PsmuxCompatibility.ValidateExpectedBinarySha256(
            expectedBinarySha256,
            nameof(expectedBinarySha256));
        DataDirectory = PsmuxCompatibility.NormalizeDataDirectory(
            dataDirectory,
            nameof(dataDirectory));
        NamespaceName = PsmuxCompatibility.ValidateNamespaceName(
            namespaceName,
            nameof(namespaceName));
        LogSink = logSink;
    }

    /// <summary>Gets the local absolute psmux client executable path.</summary>
    public string ExecutablePath { get; }

    /// <summary>Gets the expected executable SHA-256 in lowercase hexadecimal.</summary>
    public string ExpectedBinarySha256 { get; }

    /// <summary>Gets the canonical isolated data-directory path on a fixed local Windows drive.</summary>
    public string DataDirectory { get; }

    /// <summary>Gets the explicit non-default psmux namespace.</summary>
    public string NamespaceName { get; }

    /// <summary>Gets the optional sink for the library's diagnostics.</summary>
    public Action<TmuxLogEntry>? LogSink { get; }
}
