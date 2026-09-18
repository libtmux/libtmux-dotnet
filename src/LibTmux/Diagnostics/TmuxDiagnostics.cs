namespace LibTmux;

/// <summary>Names the diagnostic sources this library publishes.</summary>
/// <remarks>
/// Every tmux command is traced on the activity source and measured on the
/// meter named here. Subscribing takes the name, not a reference, so a caller
/// wires OpenTelemetry without this library referencing it:
/// <c>builder.AddSource(TmuxDiagnostics.ActivitySourceName)</c> and
/// <c>builder.AddMeter(TmuxDiagnostics.MeterName)</c>.
/// </remarks>
public static class TmuxDiagnostics
{
    /// <summary>The activity source name every tmux command is traced under.</summary>
    public const string ActivitySourceName = "LibTmux";

    /// <summary>The meter name every tmux command is measured under.</summary>
    public const string MeterName = "LibTmux";

    /// <summary>The histogram recording how long each tmux command took, in seconds.</summary>
    public const string CommandDurationInstrumentName = "libtmux.command.duration";
}
