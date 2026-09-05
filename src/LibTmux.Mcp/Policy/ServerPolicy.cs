using System.Globalization;
using Microsoft.Extensions.Logging;

namespace LibTmux.Mcp;

/// <summary>What this server will do, and how much of it at once.</summary>
/// <remarks>
/// Every knob is resolved once at startup from the environment. A bad or
/// out-of-range value is clamped and logged rather than refused: an operator
/// who mistypes a ceiling gets a working server and a warning, not a client
/// that fails to start with nothing to read.
/// </remarks>
public sealed record ServerPolicy
{
    /// <summary>The retired environment variable rejected by startup migration handling.</summary>
    public const string SafetyVariable = "LIBTMUX_SAFETY";

    /// <summary>The environment variable naming the wait ceiling in seconds.</summary>
    public const string WaitCeilingVariable = "LIBTMUX_MCP_WAIT_MAX_SECONDS";

    /// <summary>The environment variable naming the default line budget.</summary>
    public const string MaxLinesVariable = "LIBTMUX_MCP_MAX_LINES";

    /// <summary>The environment variable naming the response byte budget.</summary>
    public const string MaxBytesVariable = "LIBTMUX_MCP_MAX_BYTES";

    /// <summary>Ceiling applied when nothing names one.</summary>
    public const double DefaultWaitCeilingSeconds = 30.0;

    /// <summary>Lines a capture answers when the caller names no budget.</summary>
    public const int DefaultMaxLines = 500;

    /// <summary>Bytes a single content-bearing result may carry.</summary>
    public const int DefaultMaxBytes = 128_000;

    private const double WaitCeilingFloorSeconds = 1.0;
    private const double WaitCeilingLimitSeconds = 600.0;
    private const int MaxLinesFloor = 10;
    private const int MaxLinesLimit = 100_000;
    private const int MaxBytesFloor = 4_000;
    private const int MaxBytesLimit = 4_000_000;

    /// <summary>Gets the longest a single wait may block before reporting a timeout.</summary>
    /// <remarks>
    /// The ceiling bounds the model's turn rather than the transport: waits
    /// await throughout, so a long one does not stall other calls. What an
    /// unbounded wait costs is the turn itself — a badly chosen pattern would
    /// spend all of it with nothing to show. Client-managed MCP tasks cover
    /// work that legitimately outlives one synchronous request.
    /// </remarks>
    public TimeSpan WaitCeiling { get; init; } = TimeSpan.FromSeconds(DefaultWaitCeilingSeconds);

    /// <summary>Gets the line budget a capture uses when the caller names none.</summary>
    public int MaxLines { get; init; } = DefaultMaxLines;

    /// <summary>Gets the byte budget one content-bearing result may carry.</summary>
    public int MaxBytes { get; init; } = DefaultMaxBytes;

    /// <summary>Reads the policy out of a set of environment variables.</summary>
    /// <param name="read">Answers an environment variable, or null when it is unset.</param>
    /// <param name="logger">Records a clamped or unreadable value.</param>
    /// <returns>The resolved policy.</returns>
    public static ServerPolicy FromEnvironment(
        Func<string, string?> read,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        return new ServerPolicy
        {
            WaitCeiling = TimeSpan.FromSeconds(ParseDouble(
                read(WaitCeilingVariable),
                WaitCeilingVariable,
                DefaultWaitCeilingSeconds,
                WaitCeilingFloorSeconds,
                WaitCeilingLimitSeconds,
                logger)),
            MaxLines = ParseInt(
                read(MaxLinesVariable),
                MaxLinesVariable,
                DefaultMaxLines,
                MaxLinesFloor,
                MaxLinesLimit,
                logger),
            MaxBytes = ParseInt(
                read(MaxBytesVariable),
                MaxBytesVariable,
                DefaultMaxBytes,
                MaxBytesFloor,
                MaxBytesLimit,
                logger),
        };
    }

    /// <summary>Lowers a caller's timeout to the ceiling this policy sets.</summary>
    /// <param name="requested">What the caller asked for, or null for the ceiling.</param>
    /// <returns>The duration a wait will actually use.</returns>
    /// <remarks>
    /// An over-large request is honoured at the ceiling rather than refused,
    /// and the result reports the value used, so a model learns the policy
    /// from what came back instead of from a failed call.
    /// </remarks>
    public TimeSpan EffectiveTimeout(TimeSpan? requested)
    {
        if (requested is not TimeSpan asked || asked <= TimeSpan.Zero)
        {
            return WaitCeiling;
        }

        return asked > WaitCeiling ? WaitCeiling : asked;
    }

    private static double ParseDouble(
        string? value,
        string name,
        double fallback,
        double floor,
        double limit,
        ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || !double.IsFinite(parsed))
        {
            if (logger is not null)
            {
                Log.UnrecognisedSetting(
                    logger,
                    name,
                    value,
                    fallback.ToString(CultureInfo.InvariantCulture));
            }

            return fallback;
        }

        return Clamp(parsed, floor, limit, name, value, logger);
    }

    private static int ParseInt(
        string? value,
        string name,
        int fallback,
        int floor,
        int limit,
        ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            if (logger is not null)
            {
                Log.UnrecognisedSetting(
                    logger,
                    name,
                    value,
                    fallback.ToString(CultureInfo.InvariantCulture));
            }

            return fallback;
        }

        return (int)Clamp(parsed, floor, limit, name, value, logger);
    }

    private static double Clamp(
        double parsed,
        double floor,
        double limit,
        string name,
        string value,
        ILogger? logger)
    {
        double clamped = Math.Clamp(parsed, floor, limit);
        if (clamped != parsed && logger is not null)
        {
            Log.ClampedSetting(logger, name, value, clamped);
        }

        return clamped;
    }

}
