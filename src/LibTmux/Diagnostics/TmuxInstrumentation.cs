using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LibTmux.Internal;

/// <summary>Traces and measures one run of tmux.</summary>
/// <remarks>
/// The dispatcher is the one place every command passes through, so a span and
/// a duration are produced here rather than at the hundreds of call sites.
/// Both are free when nothing is listening: <see cref="ActivitySource" />
/// answers null without a listener, and the histogram is asked whether it is
/// enabled before a measurement is assembled.
/// </remarks>
internal static class TmuxInstrumentation
{
    private static readonly ActivitySource Source = new(TmuxDiagnostics.ActivitySourceName);

    private static readonly Meter Meter = new(TmuxDiagnostics.MeterName);

    private static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        TmuxDiagnostics.CommandDurationInstrumentName,
        "s",
        "Time one tmux command took, from dispatch to its answer.");

    /// <summary>Starts a span for one tmux command, or returns null if nothing traces.</summary>
    internal static Activity? StartCommand(IReadOnlyList<string> arguments, string? socket)
    {
        Activity? activity = Source.StartActivity(
            arguments.Count > 0 ? arguments[0] : "tmux",
            ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("tmux.subcommand", arguments.Count > 0 ? arguments[0] : null);
        activity.SetTag("tmux.socket", socket);
        return activity;
    }

    /// <summary>Records what the command answered on the span and the histogram.</summary>
    internal static void Complete(
        Activity? activity,
        long startTimestamp,
        IReadOnlyList<string> arguments,
        string? socket,
        int exitCode)
    {
        if (activity is not null)
        {
            activity.SetTag("tmux.exit_code", exitCode);
            activity.SetStatus(
                exitCode == 0 ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        }

        Record(startTimestamp, arguments, socket, exitCode);
    }

    /// <summary>Records a command that threw before it could answer.</summary>
    internal static void Fail(
        Activity? activity,
        long startTimestamp,
        IReadOnlyList<string> arguments,
        string? socket,
        Exception error)
    {
        if (activity is not null)
        {
            activity.SetTag("error.type", error.GetType().FullName);
            activity.SetStatus(ActivityStatusCode.Error, error.Message);
        }

        Record(startTimestamp, arguments, socket, exitCode: null);
    }

    private static void Record(
        long startTimestamp,
        IReadOnlyList<string> arguments,
        string? socket,
        int? exitCode)
    {
        if (!CommandDuration.Enabled)
        {
            return;
        }

        CommandDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds,
            new KeyValuePair<string, object?>(
                "tmux.subcommand",
                arguments.Count > 0 ? arguments[0] : null),
            new KeyValuePair<string, object?>("tmux.socket", socket),
            new KeyValuePair<string, object?>("tmux.exit_code", exitCode));
    }
}
