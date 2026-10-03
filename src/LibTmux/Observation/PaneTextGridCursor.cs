using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace LibTmux.Internal;

internal interface IPaneTextGridCursor
{
    public string PanePid { get; }
    public int HistorySize { get; }
    public int PaneHeight { get; }
    public int AnchorAbsolute { get; }
    public string? AnchorHash { get; }
    public int BelowCount { get; }
    public string? BelowHash { get; }
    public int SuffixCount { get; }
    public string? SuffixHash { get; }
    public byte[]? TrackedRowDigests();
}

[UnsupportedOSPlatform("windows")]
internal sealed record PaneTextGridCursor(
    string PanePid,
    int HistorySize,
    int PaneHeight,
    int AnchorAbsolute,
    string? AnchorHash,
    int BelowCount,
    string? BelowHash,
    int SuffixCount,
    string? SuffixHash,
    string? RowHashes) : IPaneTextGridCursor
{
    private const int MaximumBelowRows = 32;
    private const int RowDigestBytes = 8;

    internal static PaneTextGridCursor Build(
        PaneTextGridState state,
        IReadOnlyList<string> cursorRows)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(cursorRows);
        int suffixCount = Math.Max(cursorRows.Count - 1, 0);
        int belowCount = Math.Min(suffixCount, MaximumBelowRows);
        return new PaneTextGridCursor(
            state.PanePid,
            state.HistorySize,
            state.PaneHeight,
            state.CursorAbsolute,
            cursorRows.Count > 0 ? HashLine(cursorRows[0]) : null,
            belowCount,
            belowCount > 0 ? HashRows(cursorRows, 1, belowCount) : null,
            suffixCount,
            suffixCount > 0 ? HashRows(cursorRows, 1, suffixCount) : null,
            belowCount > 0 ? HashRowWindow(cursorRows, 1, belowCount) : null);
    }

    internal static string HashLine(string line) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line))).ToLowerInvariant();

    internal static string HashRows(IReadOnlyList<string> rows, int start, int count)
    {
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        for (int index = start; index < start + count; index++)
        {
            byte[] text = Encoding.UTF8.GetBytes(rows[index]);
            BinaryPrimitives.WriteInt32BigEndian(length, text.Length);
            digest.AppendData(length);
            digest.AppendData(text);
        }

        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string HashRowWindow(IReadOnlyList<string> rows, int start, int count)
    {
        Span<byte> window = stackalloc byte[MaximumBelowRows * RowDigestBytes];
        for (int index = 0; index < count; index++)
        {
            RowDigest(rows[start + index]).CopyTo(window[(index * RowDigestBytes)..]);
        }

        return Convert.ToBase64String(window[..(count * RowDigestBytes)])
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public byte[]? TrackedRowDigests()
    {
        if (RowHashes is null)
        {
            return null;
        }

        string encoded = RowHashes.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) & ~3, '='));
    }

    internal static bool TrackedRowUnchanged(byte[] digests, int index, string line) =>
        RowDigest(line).SequenceEqual(digests.AsSpan(index * RowDigestBytes, RowDigestBytes));

    private static ReadOnlySpan<byte> RowDigest(string line) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(line)).AsSpan(0, RowDigestBytes);
}
