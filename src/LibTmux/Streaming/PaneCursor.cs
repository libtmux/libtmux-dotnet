using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace LibTmux.Internal;

/// <summary>Where a reader of a pane left off.</summary>
/// <remarks>
/// The fields bind the position to one endpoint, server generation, pane and
/// pane process, so a reader can tell a continued pane from a replaced one.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed record PaneCursor(
    int Version,
    string EndpointFingerprint,
    int ServerProcessId,
    long ServerStartTime,
    string PaneId,
    string PanePid,
    int HistorySize,
    int PaneHeight,
    int AnchorAbsolute,
    string? AnchorHash,
    int BelowCount,
    string? BelowHash,
    int SuffixCount,
    string? SuffixHash,
    string? RowHashes)
{
    internal const int CurrentVersion = 3;
    private const int DigestHexLength = 64;
    private const int MaximumBelowRows = 32;
    private const int RowDigestBytes = 8;

    /// <summary>Fingerprints one row.</summary>
    /// <param name="line">The row's text.</param>
    /// <returns>A stable hash of it.</returns>
    internal static string HashLine(string line)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(line));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>Fingerprints each row of a window, one truncated digest per row.</summary>
    /// <remarks>
    /// A tail result carries its cursor twice, so the window is packed rather
    /// than written as hex: every byte saved here is two bytes of a response.
    /// </remarks>
    internal static string HashRowWindow(IReadOnlyList<string> rows, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, rows.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, rows.Count - count);

        Span<byte> window = stackalloc byte[MaximumBelowRows * RowDigestBytes];
        for (int index = 0; index < count; index++)
        {
            RowDigest(rows[start + index])
                .CopyTo(window[(index * RowDigestBytes)..]);
        }

        return ToBase64Url(window[..(count * RowDigestBytes)]);
    }

    /// <summary>The recorded digest of each tracked row, or null when none was.</summary>
    /// <returns>The packed digests, one <c>RowDigestBytes</c> run per row.</returns>
    internal byte[]? TrackedRowDigests() =>
        RowHashes is null ? null : FromBase64Url(RowHashes);

    /// <summary>Answers whether a tracked row still holds the text it was seen with.</summary>
    /// <param name="digests">The digests <see cref="TrackedRowDigests" /> returned.</param>
    /// <param name="index">The row's position within the tracked window.</param>
    /// <param name="line">The row's text now.</param>
    /// <returns><see langword="true" /> when the row is unchanged.</returns>
    internal static bool TrackedRowUnchanged(byte[] digests, int index, string line)
    {
        ArgumentNullException.ThrowIfNull(digests);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(line);
        return RowDigest(line)
            .SequenceEqual(digests.AsSpan(index * RowDigestBytes, RowDigestBytes));
    }

    private static ReadOnlySpan<byte> RowDigest(string line) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(line)).AsSpan(0, RowDigestBytes);

    /// <summary>Fingerprints an ordered row sequence without retaining every row hash.</summary>
    internal static string HashRows(IReadOnlyList<string> rows, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, rows.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, rows.Count - count);

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

    /// <summary>Builds a cursor for where a read finished.</summary>
    /// <param name="pane">The pane and exact server endpoint that were read.</param>
    /// <param name="state">The grid state the read saw.</param>
    /// <param name="cursorRows">The rows from the cursor row to the visible bottom.</param>
    /// <returns>The cursor.</returns>
    internal static PaneCursor Build(
        Pane pane,
        PaneGridState state,
        IReadOnlyList<string> cursorRows)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(cursorRows);

        ServerGeneration generation = pane.Generation;
        string endpoint = pane.Server.Connection?.GetEndpointFingerprint()
            ?? throw new IncompleteSnapshotException("connection", SnapshotDepth.Server);
        // The cap keeps fallback anchor scans linear even on a repetitive, very tall pane.
        int suffixCount = Math.Max(cursorRows.Count - 1, 0);
        int belowCount = Math.Min(suffixCount, MaximumBelowRows);
        var cursor = new PaneCursor(
            Version: CurrentVersion,
            EndpointFingerprint: endpoint,
            ServerProcessId: generation.ProcessId,
            ServerStartTime: generation.StartTime,
            PaneId: pane.Id.ToString(),
            PanePid: state.PanePid,
            HistorySize: state.HistorySize,
            PaneHeight: state.PaneHeight,
            AnchorAbsolute: state.CursorAbsolute,
            AnchorHash: cursorRows.Count > 0 ? HashLine(cursorRows[0]) : null,
            BelowCount: belowCount,
            BelowHash: belowCount > 0 ? HashRows(cursorRows, 1, belowCount) : null,
            SuffixCount: suffixCount,
            SuffixHash: suffixCount > 0 ? HashRows(cursorRows, 1, suffixCount) : null,
            RowHashes: belowCount > 0 ? HashRowWindow(cursorRows, 1, belowCount) : null);
        cursor.Validate();
        return cursor;
    }

    /// <summary>Rejects a cursor whose fields cannot describe one pane position.</summary>
    /// <exception cref="ArgumentException">A field is out of range or malformed.</exception>
    internal void Validate()
    {
        bool canonicalPaneId = LibTmux.PaneId.TryParse(PaneId, out LibTmux.PaneId parsedPane)
            && string.Equals(parsedPane.ToString(), PaneId, StringComparison.Ordinal);
        bool canonicalPanePid = int.TryParse(
                PanePid,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int panePid)
            && panePid > 0
            && string.Equals(
                panePid.ToString(CultureInfo.InvariantCulture),
                PanePid,
                StringComparison.Ordinal);
        long lastRow = (long)HistorySize + PaneHeight - 1;

        if (Version != CurrentVersion
            || !IsDigest(EndpointFingerprint)
            || ServerProcessId <= 0
            || ServerStartTime <= 0
            || !canonicalPaneId
            || !canonicalPanePid
            || HistorySize < 0
            || PaneHeight <= 0
            || AnchorAbsolute < HistorySize
            || AnchorAbsolute > lastRow + (AnchorHash is null ? 1 : 0)
            || (AnchorHash is not null && !IsDigest(AnchorHash))
            || BelowCount < 0
            || BelowCount > MaximumBelowRows
            || BelowCount >= PaneHeight
            || BelowCount > Math.Max(lastRow - AnchorAbsolute, 0)
            || (BelowHash is not null && !IsDigest(BelowHash))
            || (BelowCount == 0) != (BelowHash is null)
            || SuffixCount < 0
            || SuffixCount >= PaneHeight
            || SuffixCount > Math.Max(lastRow - AnchorAbsolute, 0)
            || BelowCount != Math.Min(SuffixCount, MaximumBelowRows)
            || (SuffixHash is not null && !IsDigest(SuffixHash))
            || (SuffixCount == 0) != (SuffixHash is null)
            || (SuffixCount == BelowCount
                && !string.Equals(SuffixHash, BelowHash, StringComparison.Ordinal))
            || (BelowCount == 0) != (RowHashes is null)
            || (RowHashes is not null && !IsRowWindow(RowHashes, BelowCount))
            || (AnchorHash is null
                && (BelowCount != 0
                    || BelowHash is not null
                    || SuffixCount != 0
                    || SuffixHash is not null)))
        {
            throw new ArgumentException("The pane cursor is malformed.");
        }
    }

    private static bool IsDigest(string? value) =>
        value is { Length: DigestHexLength }
        && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsRowWindow(string value, int count)
    {
        try
        {
            return FromBase64Url(value).Length == count * RowDigestBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static string ToBase64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal static byte[] FromBase64Url(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty
            || value.Length % 4 == 1
            || value.Contains('=')
            || value.Contains('+')
            || value.Contains('/')
            || value.IndexOfAnyExcept(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) >= 0)
        {
            throw new FormatException("The value is not canonical base64url.");
        }

        string encoded = value.ToString().Replace('-', '+').Replace('_', '/');
        byte[] decoded = Convert.FromBase64String(
            encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
        if (!ToBase64Url(decoded).AsSpan().SequenceEqual(value))
        {
            throw new FormatException("The value is not canonical base64url.");
        }

        return decoded;
    }

}
