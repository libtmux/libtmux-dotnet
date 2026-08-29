namespace LibTmux;

internal sealed class ControlModeLimits
{
    private const int DefaultMaxLineBytes = 64 * 1024;
    private const int DefaultStandardErrorTailBytes = 64 * 1024;

    internal ControlModeLimits(
        int maxLineBytes = DefaultMaxLineBytes,
        int standardErrorTailBytes = DefaultStandardErrorTailBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(standardErrorTailBytes);
        MaxLineBytes = maxLineBytes;
        StandardErrorTailBytes = standardErrorTailBytes;
    }

    internal int MaxLineBytes { get; }

    internal int StandardErrorTailBytes { get; }
}
