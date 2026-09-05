using Microsoft.Extensions.Logging;

namespace LibTmux.Mcp;

/// <summary>Every log message this server writes.</summary>
/// <remarks>
/// Source-generated rather than written by hand so that a message costs
/// nothing when its level is disabled. They all reach standard error: the
/// protocol owns standard output, and a line written to the wrong stream
/// disconnects the client rather than showing up in a log.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Unrecognised {Variable}={Value}; falling back to {Fallback}.")]
    internal static partial void UnrecognisedSetting(
        ILogger logger,
        string variable,
        string value,
        string fallback);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "{Variable}={Value} is out of range; clamped to {Clamped}.")]
    internal static partial void ClampedSetting(
        ILogger logger,
        string variable,
        string value,
        double clamped);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Could not reach the tmux server on socket {Socket}.")]
    internal static partial void ServerUnreachable(ILogger logger, Exception error, string? socket);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "Control client for socket {Socket} ended: {Reason}")]
    internal static partial void ControlClientEnded(ILogger logger, string? socket, string? reason);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Debug,
        Message = "Control client for socket {Socket} could not start; falling back to polling.")]
    internal static partial void ControlClientUnavailable(
        ILogger logger,
        Exception error,
        string? socket);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "Tool {Tool} failed.")]
    internal static partial void ToolFailed(ILogger logger, Exception error, string tool);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Debug,
        Message = "Control client for socket {Socket} could not be cleaned up.")]
    internal static partial void ControlClientCleanupFailed(
        ILogger logger,
        Exception error,
        string? socket);

}
