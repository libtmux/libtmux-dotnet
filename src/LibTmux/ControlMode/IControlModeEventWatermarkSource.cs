namespace LibTmux.Internal;

/// <summary>Supplies one destructive control-event reader and its written-event watermark.</summary>
internal interface IControlModeEventWatermarkSource
{
    internal long CaptureEventWatermark();

    internal ControlModeEventBuffer.Reader CreateEventReader(CancellationToken cancellationToken);
}
