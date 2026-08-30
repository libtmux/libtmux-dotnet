using System.Buffers;
using System.Text;

namespace LibTmux.Query;

internal static class QueryTextSemantics
{
    internal static bool TryCountScalars(string? value, out int count)
    {
        count = 0;
        if (value is null)
        {
            return false;
        }

        ReadOnlySpan<char> remaining = value;
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(
                remaining,
                out _,
                out int consumed);
            if (status != OperationStatus.Done)
            {
                count = 0;
                return false;
            }

            remaining = remaining[consumed..];
            count++;
        }

        return true;
    }
}
