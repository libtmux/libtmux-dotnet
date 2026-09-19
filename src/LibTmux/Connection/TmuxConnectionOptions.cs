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
/// <remarks>
/// Every option is set through an initializer. A constructor parameter binds
/// at the call site when it compiles, so adding one would break an assembly
/// already built against the old signature - and this is the type most likely
/// to grow one.
/// </remarks>
public sealed record ServerConnectionOptions
{
    private readonly string _tmuxBinaryPath = "tmux";
    private readonly string? _socketName;
    private readonly string? _socketPath;
    private readonly string? _configurationFile;
    private readonly TmuxColorMode _colorMode;
    private readonly IReadOnlyDictionary<string, string?>? _childEnvironment;
    private readonly TimeSpan? _commandTimeout;
    private readonly int? _maxCapturedBytesPerStream;
    private readonly int? _controlModeEventBufferCapacity;

    /// <summary>Gets the tmux executable path.</summary>
    /// <exception cref="ArgumentException">The path is empty or whitespace.</exception>
    public string TmuxBinaryPath
    {
        get => _tmuxBinaryPath;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _tmuxBinaryPath = value;
        }
    }

    /// <summary>Gets the explicit socket name.</summary>
    /// <exception cref="ArgumentException">The name is empty or whitespace.</exception>
    public string? SocketName
    {
        get => _socketName;
        init
        {
            if (value is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
            }

            _socketName = value;
        }
    }

    /// <summary>Gets the explicit socket path.</summary>
    /// <exception cref="ArgumentException">The path is empty or whitespace.</exception>
    public string? SocketPath
    {
        get => _socketPath;
        init
        {
            if (value is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
            }

            _socketPath = value;
        }
    }

    /// <summary>Gets the deferred socket-name factory.</summary>
    public Func<string>? SocketNameFactory { get; init; }

    /// <summary>Gets the tmux configuration file.</summary>
    /// <exception cref="ArgumentException">The path is empty or whitespace.</exception>
    public string? ConfigurationFile
    {
        get => _configurationFile;
        init
        {
            if (value is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
            }

            _configurationFile = value;
        }
    }

    /// <summary>Gets the requested tmux color mode.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The mode is not a defined value.</exception>
    public TmuxColorMode ColorMode
    {
        get => _colorMode;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(ColorMode));
            }

            _colorMode = value;
        }
    }

    /// <summary>Gets the post-connect initializer.</summary>
    public Func<Server, CancellationToken, ValueTask>? InitializeAsync { get; init; }

    /// <summary>Gets the child-process environment overrides.</summary>
    /// <exception cref="ArgumentException">
    /// A name is empty, contains NUL or <c>=</c>, or a value contains NUL.
    /// </exception>
    public IReadOnlyDictionary<string, string?>? ChildEnvironment
    {
        get => _childEnvironment;
        init => _childEnvironment = CopyChildEnvironment(value);
    }

    /// <summary>Gets the connection logger.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>Gets how long one tmux command may run, or null to wait indefinitely.</summary>
    /// <remarks>
    /// A tmux that stops answering otherwise hangs the caller until its own
    /// cancellation token fires, and forever if it passed none. On expiry the
    /// command throws <see cref="TmuxTransportException" /> whose
    /// <see cref="LibTmuxException.Dispatch" /> is
    /// <see cref="TmuxDispatchState.Unknown" />: tmux may already have acted.
    /// A caller's own cancellation still wins, and reads as cancellation.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The timeout does not run forward.</exception>
    public TimeSpan? CommandTimeout
    {
        get => _commandTimeout;
        init
        {
            if (value is TimeSpan limit && limit <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(CommandTimeout),
                    limit,
                    "A command timeout runs forward.");
            }

            _commandTimeout = value;
        }
    }

    /// <summary>Gets the largest output one command may capture, in bytes.</summary>
    /// <remarks>
    /// Defaults to 64 MiB. A command whose output passes it fails rather than
    /// growing without bound, so a service that runs many captures at once can
    /// bound what one of them costs. Raise it for a capture that legitimately
    /// needs more.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The ceiling does not count upward.</exception>
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
    /// <exception cref="ArgumentOutOfRangeException">The capacity holds no events.</exception>
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

    internal PsmuxPreviewOptions? PsmuxPreview { get; init; }

    private static ReadOnlyDictionary<string, string?>? CopyChildEnvironment(
        IReadOnlyDictionary<string, string?>? childEnvironment)
    {
        if (childEnvironment is null)
        {
            return null;
        }

        Dictionary<string, string?> copy = new(StringComparer.Ordinal);
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

            copy.Add(key, value);
        }

        return new ReadOnlyDictionary<string, string?>(copy);
    }

    /// <summary>Gets conventional connection defaults.</summary>
    public static ServerConnectionOptions Default { get; } = new();

    internal static ServerConnectionOptions ForPsmux(PsmuxConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ServerConnectionOptions
        {
            TmuxBinaryPath = options.ExecutablePath,
            SocketName = options.NamespaceName,
            Logger = options.Logger,
            PsmuxPreview = new PsmuxPreviewOptions(
                options.ExpectedBinarySha256,
                options.DataDirectory),
        };
    }
}
