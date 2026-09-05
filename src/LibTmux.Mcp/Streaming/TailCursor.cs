using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <summary>Where a reader of a pane left off.</summary>
/// <remarks>
/// The token is authenticated with a process-local key and bound to the exact
/// endpoint, server generation, and pane that issued it. Restarting this MCP
/// server invalidates its cursors instead of accepting unauthenticated state
/// from an earlier process.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed record TailCursor(
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
    private const int CurrentVersion = 3;
    private const int DigestHexLength = 64;
    private const int MaximumBelowRows = 32;
    private const int RowDigestBytes = 8;
    private const int MaximumPayloadBytes = 1536;
    private const int MaximumTokenCharacters = 2048;
    private const string Prefix = "tmux-tail-v3:";
    private static readonly byte[] AuthenticationKey = RandomNumberGenerator.GetBytes(32);

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
    internal static TailCursor Build(
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
        var cursor = new TailCursor(
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

    /// <summary>Renders the cursor as the opaque token a caller passes back.</summary>
    /// <returns>The authenticated token.</returns>
    public string Encode()
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(this, TailCursorJson.Default.TailCursor);
        byte[] signature = HMACSHA256.HashData(AuthenticationKey, payload);
        return Prefix + ToBase64Url(payload) + "." + ToBase64Url(signature);
    }

    /// <summary>Reads and binds a token a caller passed back.</summary>
    /// <param name="token">The token, or null when the caller sent none.</param>
    /// <param name="pane">The exact pane the token must have been issued for.</param>
    /// <returns>The cursor, or null when there was no token.</returns>
    /// <exception cref="McpException">The token is invalid or belongs elsewhere.</exception>
    public static TailCursor? Decode(string? token, Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        if (token is null)
        {
            return null;
        }

        string value = token;
        if (value.Length > MaximumTokenCharacters
            || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw InvalidCursor();
        }

        ReadOnlySpan<char> body = value.AsSpan(Prefix.Length);
        int separator = body.IndexOf('.');
        if (separator <= 0 || separator != body.LastIndexOf('.'))
        {
            throw InvalidCursor();
        }

        try
        {
            byte[] payload = FromBase64Url(body[..separator]);
            byte[] signature = FromBase64Url(body[(separator + 1)..]);
            if (payload.Length is 0 or > MaximumPayloadBytes
                || signature.Length != HMACSHA256.HashSizeInBytes)
            {
                throw InvalidCursor();
            }

            byte[] expectedSignature = HMACSHA256.HashData(AuthenticationKey, payload);
            if (!CryptographicOperations.FixedTimeEquals(signature, expectedSignature))
            {
                throw InvalidCursor();
            }

            ValidateJsonShape(payload);
            TailCursor cursor = JsonSerializer.Deserialize(
                    payload,
                    TailCursorJson.Default.TailCursor)
                ?? throw InvalidCursor();
            cursor.Validate();
            cursor.ValidateBinding(pane);
            return cursor;
        }
        catch (Exception error) when (error is FormatException
            or JsonException
            or OverflowException
            or ArgumentException)
        {
            throw InvalidCursor();
        }
    }

    private void Validate()
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
            throw InvalidCursor();
        }
    }

    private void ValidateBinding(Pane pane)
    {
        ServerGeneration generation = pane.Generation;
        string endpoint = pane.Server.Connection?.GetEndpointFingerprint()
            ?? throw InvalidCursor();
        if (!string.Equals(PaneId, pane.Id.ToString(), StringComparison.Ordinal)
            || ServerProcessId != generation.ProcessId
            || ServerStartTime != generation.StartTime
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(EndpointFingerprint),
                Encoding.ASCII.GetBytes(endpoint)))
        {
            throw new McpException(
                "That capture_since cursor belongs to a different pane or tmux server. "
                + "Call capture_since without a cursor to start again here.");
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
        catch (Exception error) when (error is FormatException or McpException)
        {
            return false;
        }
    }

    private static void ValidateJsonShape(ReadOnlySpan<byte> payload)
    {
        var reader = new Utf8JsonReader(
            payload,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2,
            });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw InvalidCursor();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw InvalidCursor();
            }

            string property = reader.GetString() ?? throw InvalidCursor();
            if (!KnownProperties.Contains(property) || !seen.Add(property) || !reader.Read())
            {
                throw InvalidCursor();
            }

            if (reader.TokenType is JsonTokenType.StartArray
                or JsonTokenType.StartObject
                or JsonTokenType.EndArray
                or JsonTokenType.EndObject
                or JsonTokenType.PropertyName)
            {
                throw InvalidCursor();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject
            || reader.Read()
            || seen.Count != KnownProperties.Count)
        {
            throw InvalidCursor();
        }
    }

    private static readonly HashSet<string> KnownProperties = new(
        [
            "version",
            "endpointFingerprint",
            "serverProcessId",
            "serverStartTime",
            "paneId",
            "panePid",
            "historySize",
            "paneHeight",
            "anchorAbsolute",
            "anchorHash",
            "belowCount",
            "belowHash",
            "suffixCount",
            "suffixHash",
            "rowHashes",
        ],
        StringComparer.Ordinal);

    private static string ToBase64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty
            || value.Length % 4 == 1
            || value.Contains('=')
            || value.Contains('+')
            || value.Contains('/')
            || value.IndexOfAnyExcept(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) >= 0)
        {
            throw InvalidCursor();
        }

        string encoded = value.ToString().Replace('-', '+').Replace('_', '/');
        byte[] decoded = Convert.FromBase64String(
            encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
        if (!ToBase64Url(decoded).AsSpan().SequenceEqual(value))
        {
            throw InvalidCursor();
        }

        return decoded;
    }

    private static McpException InvalidCursor() => new(
        "That capture_since cursor is invalid or is not one this server issued. "
        + "Omit it to start from what is on screen now.");
}

/// <summary>Serializes cursors without reflection.</summary>
[JsonSerializable(typeof(TailCursor))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal sealed partial class TailCursorJson : JsonSerializerContext;
