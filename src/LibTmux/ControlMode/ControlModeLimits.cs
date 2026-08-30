namespace LibTmux;

internal sealed class ControlModeLimits
{
    private const int DefaultMaxLineBytes = 64 * 1024;
    private const int DefaultMaxRequestBytes = 16 * 1024 * 1024;
    private const int DefaultMaxBlockBytes = 4 * 1024 * 1024;
    private const int DefaultMaxReplyBytes = 16 * 1024 * 1024;
    private const int DefaultStandardErrorTailBytes = 64 * 1024;

    internal ControlModeLimits(
        int maxLineBytes = DefaultMaxLineBytes,
        int standardErrorTailBytes = DefaultStandardErrorTailBytes,
        int maxPendingCommands = 256,
        int maxBlockLines = 4096,
        int maxBlockBytes = DefaultMaxBlockBytes,
        int maxReplyBlocks = 4096,
        int maxReplyLines = 16384,
        int maxReplyBytes = DefaultMaxReplyBytes,
        int maxRequestBytes = DefaultMaxRequestBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(standardErrorTailBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingCommands);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBlockLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBlockBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReplyBlocks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReplyLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReplyBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRequestBytes);
        MaxLineBytes = maxLineBytes;
        StandardErrorTailBytes = standardErrorTailBytes;
        MaxPendingCommands = maxPendingCommands;
        MaxBlockLines = maxBlockLines;
        MaxBlockBytes = maxBlockBytes;
        MaxReplyBlocks = maxReplyBlocks;
        MaxReplyLines = maxReplyLines;
        MaxReplyBytes = maxReplyBytes;
        MaxRequestBytes = maxRequestBytes;
    }

    internal int MaxLineBytes { get; }

    internal int StandardErrorTailBytes { get; }

    internal int MaxPendingCommands { get; }

    internal int MaxBlockLines { get; }

    internal int MaxBlockBytes { get; }

    internal int MaxReplyBlocks { get; }

    internal int MaxReplyLines { get; }

    internal int MaxReplyBytes { get; }

    internal int MaxRequestBytes { get; }
}
