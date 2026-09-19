using System.Collections.ObjectModel;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux;

internal sealed class PsmuxPreviewOptions : IEquatable<PsmuxPreviewOptions>
{
    internal PsmuxPreviewOptions(string expectedBinarySha256, string dataDirectory)
    {
        ExpectedBinarySha256 = PsmuxCompatibility.ValidateExpectedBinarySha256(
            expectedBinarySha256,
            nameof(expectedBinarySha256));
        DataDirectory = PsmuxCompatibility.NormalizeDataDirectory(
            dataDirectory,
            nameof(dataDirectory));
    }

    internal string ExpectedBinarySha256 { get; }

    internal string DataDirectory { get; }

    public bool Equals(PsmuxPreviewOptions? other) =>
        other is not null
        && string.Equals(
            ExpectedBinarySha256,
            other.ExpectedBinarySha256,
            StringComparison.Ordinal)
        && string.Equals(DataDirectory, other.DataDirectory, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as PsmuxPreviewOptions);

    public override int GetHashCode() => HashCode.Combine(ExpectedBinarySha256, DataDirectory);
}

/// <summary>Configures a tmux server connection without mutating process-wide state.</summary>
public sealed record ServerConnectionOptions
{
    private readonly int? _maxCapturedBytesPerStream;
    private readonly int? _controlModeEventBufferCapacity;

    /// <summary>Initializes connection options.</summary>
    public ServerConnectionOptions(
        string tmuxBinaryPath = "tmux",
        string? socketName = null,
        string? socketPath = null,
        Func<string>? socketNameFactory = null,
        string? configurationFile = null,
        TmuxColorMode colorMode = TmuxColorMode.Default,
        Func<Server, CancellationToken, ValueTask>? initializeAsync = null,
        IReadOnlyDictionary<string, string?>? childEnvironment = null,
        ILogger? logger = null,
        TimeSpan? commandTimeout = null)
        : this(
            tmuxBinaryPath,
            socketName,
            socketPath,
            socketNameFactory,
            configurationFile,
            colorMode,
            initializeAsync,
            childEnvironment,
            logger,
            commandTimeout,
            psmuxPreview: null)
    {
    }

    private ServerConnectionOptions(
        string tmuxBinaryPath,
        string? socketName,
        string? socketPath,
        Func<string>? socketNameFactory,
        string? configurationFile,
        TmuxColorMode colorMode,
        Func<Server, CancellationToken, ValueTask>? initializeAsync,
        IReadOnlyDictionary<string, string?>? childEnvironment,
        ILogger? logger,
        TimeSpan? commandTimeout,
        PsmuxPreviewOptions? psmuxPreview)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tmuxBinaryPath);
        if (socketName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(socketName);
        }

        if (socketPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        }

        if (configurationFile is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configurationFile);
        }

        if (!Enum.IsDefined(colorMode))
        {
            throw new ArgumentOutOfRangeException(nameof(colorMode));
        }

        if (commandTimeout is TimeSpan limit && limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout),
                limit,
                "A command timeout runs forward.");
        }

        Dictionary<string, string?>? childEnvironmentCopy = null;
        if (childEnvironment is not null)
        {
            childEnvironmentCopy = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach ((string key, string? value) in childEnvironment)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(key);
                if (key.Contains('\0') || key.Contains('='))
                {
                    throw new ArgumentException(
                        "Child environment variable names cannot contain NUL or '='.",
                        nameof(childEnvironment));
                }

                if (value is not null && value.Contains('\0'))
                {
                    throw new ArgumentException(
                        "Child environment variable values cannot contain NUL.",
                        nameof(childEnvironment));
                }

                childEnvironmentCopy.Add(key, value);
            }
        }

        if (psmuxPreview is not null)
        {
            if (!Path.IsPathFullyQualified(tmuxBinaryPath))
            {
                throw new ArgumentException(
                    "The psmux preview requires a fully qualified executable path.",
                    nameof(tmuxBinaryPath));
            }

            if (socketName is null && socketNameFactory is null)
            {
                throw new ArgumentException(
                    "The psmux preview requires an explicit socket name or socket-name factory.",
                    nameof(socketName));
            }

            string[] reservedVariables =
            [
                "LIBTMUX_SOCKET_NAME",
                "LIBTMUX_SOCKET_PATH",
                "TMUX",
                "PSMUX_ACTIVE",
                "PSMUX_CLIENT_LAST_SESSION",
                "PSMUX_CONFIG_FILE",
                "PSMUX_DATA_DIR",
                "PSMUX_DEFAULT_SESSION",
                "PSMUX_SESSION",
                "PSMUX_SESSION_NAME",
                "PSMUX_SWITCH_TO",
                "PSMUX_TARGET_FULL",
                "PSMUX_TARGET_SESSION",
            ];
            if (childEnvironmentCopy is not null
                && childEnvironmentCopy.Keys.Any(key => reservedVariables.Contains(
                    key,
                    StringComparer.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    "The psmux preview owns its routing environment variables.",
                    nameof(childEnvironment));
            }
        }

        TmuxBinaryPath = tmuxBinaryPath;
        SocketName = socketName;
        SocketPath = socketPath;
        SocketNameFactory = socketNameFactory;
        ConfigurationFile = configurationFile;
        ColorMode = colorMode;
        InitializeAsync = initializeAsync;
        ChildEnvironment = childEnvironmentCopy is null
            ? null
            : new ReadOnlyDictionary<string, string?>(childEnvironmentCopy);
        Logger = logger;
        CommandTimeout = commandTimeout;
        PsmuxPreview = psmuxPreview;
    }

    /// <summary>Gets conventional connection defaults.</summary>
    public static ServerConnectionOptions Default { get; } = new();

    internal static ServerConnectionOptions ForPsmux(PsmuxConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ServerConnectionOptions(
            tmuxBinaryPath: options.ExecutablePath,
            socketName: options.NamespaceName,
            socketPath: null,
            socketNameFactory: null,
            configurationFile: null,
            colorMode: TmuxColorMode.Default,
            initializeAsync: null,
            childEnvironment: null,
            logger: options.Logger,
            commandTimeout: null,
            psmuxPreview: new PsmuxPreviewOptions(
                options.ExpectedBinarySha256,
                options.DataDirectory));
    }

    /// <summary>Gets the tmux executable path.</summary>
    public string TmuxBinaryPath { get; }

    /// <summary>Gets the explicit socket name.</summary>
    public string? SocketName { get; }

    /// <summary>Gets the explicit socket path.</summary>
    public string? SocketPath { get; }

    /// <summary>Gets the deferred socket-name factory.</summary>
    public Func<string>? SocketNameFactory { get; }

    /// <summary>Gets the tmux configuration file.</summary>
    public string? ConfigurationFile { get; }

    /// <summary>Gets the requested tmux color mode.</summary>
    public TmuxColorMode ColorMode { get; }

    /// <summary>Gets the post-connect initializer.</summary>
    public Func<Server, CancellationToken, ValueTask>? InitializeAsync { get; }

    /// <summary>Gets the child-process environment overrides.</summary>
    public IReadOnlyDictionary<string, string?>? ChildEnvironment { get; }

    /// <summary>Gets the connection logger.</summary>
    public ILogger? Logger { get; }

    /// <summary>Gets how long one tmux command may run, or null to wait indefinitely.</summary>
    /// <remarks>
    /// A tmux that stops answering otherwise hangs the caller until its own
    /// cancellation token fires, and forever if it passed none. On expiry the
    /// command throws <see cref="TmuxTransportException" /> whose
    /// <see cref="LibTmuxException.Dispatch" /> is
    /// <see cref="TmuxDispatchState.Unknown" />: tmux may already have acted.
    /// A caller's own cancellation still wins, and reads as cancellation.
    /// </remarks>
    public TimeSpan? CommandTimeout { get; }

    /// <summary>Gets the largest output one command may capture, in bytes.</summary>
    /// <remarks>
    /// Defaults to 64 MiB. A command whose output passes it fails rather than
    /// growing without bound, so a service that runs many captures at once can
    /// bound what one of them costs. Raise it for a capture that legitimately
    /// needs more. Set as an initializer, not a constructor argument: the
    /// constructor's shape is a promise to every compiled caller, and an
    /// option added to it breaks them.
    /// </remarks>
    public int? MaxCapturedBytesPerStream
    {
        get => _maxCapturedBytesPerStream;
        init
        {
            if (value is int bytes && bytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxCapturedBytesPerStream),
                    bytes,
                    "A capture ceiling counts upward.");
            }

            _maxCapturedBytesPerStream = value;
        }
    }

    /// <summary>Gets how many control-mode events are buffered before the oldest are dropped.</summary>
    /// <remarks>
    /// Defaults to 512. A consumer slower than its panes loses the oldest
    /// events and is told so by <see cref="TmuxEventsDroppedEvent" />; raising
    /// this buys time rather than memory without bound.
    /// </remarks>
    public int? ControlModeEventBufferCapacity
    {
        get => _controlModeEventBufferCapacity;
        init
        {
            if (value is int events && events <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ControlModeEventBufferCapacity),
                    events,
                    "An event buffer holds at least one event.");
            }

            _controlModeEventBufferCapacity = value;
        }
    }

    internal PsmuxPreviewOptions? PsmuxPreview { get; }
}
