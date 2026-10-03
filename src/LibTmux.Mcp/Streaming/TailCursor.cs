using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibTmux.Internal;
using ModelContextProtocol;

namespace LibTmux.Mcp;

/// <summary>Encodes pane cursors as the opaque tokens capture_since hands out.</summary>
/// <remarks>
/// The token is authenticated with a process-local key and bound to the exact
/// endpoint, server generation, and pane that issued it. Restarting this MCP
/// server invalidates its cursors instead of accepting unauthenticated state
/// from an earlier process.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class TailCursor
{
    private const int MaximumPayloadBytes = 1536;
    private const int MaximumTokenCharacters = 2048;
    private const string Prefix = "tmux-tail-v3:";
    private static readonly byte[] AuthenticationKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Renders the cursor as the opaque token a caller passes back.</summary>
    /// <returns>The authenticated token.</returns>
    public static string Encode(this PaneCursor cursor)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(cursor, TailCursorJson.Default.PaneCursor);
        byte[] signature = HMACSHA256.HashData(AuthenticationKey, payload);
        return Prefix + PaneCursor.ToBase64Url(payload) + "." + PaneCursor.ToBase64Url(signature);
    }

    /// <summary>Reads and binds a token a caller passed back.</summary>
    /// <param name="token">The token, or null when the caller sent none.</param>
    /// <param name="pane">The exact pane the token must have been issued for.</param>
    /// <returns>The cursor, or null when there was no token.</returns>
    /// <exception cref="McpException">The token is invalid or belongs elsewhere.</exception>
    public static PaneCursor? Decode(string? token, Pane pane)
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
            byte[] payload = PaneCursor.FromBase64Url(body[..separator]);
            byte[] signature = PaneCursor.FromBase64Url(body[(separator + 1)..]);
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
            PaneCursor cursor = JsonSerializer.Deserialize(
                    payload,
                    TailCursorJson.Default.PaneCursor)
                ?? throw InvalidCursor();
            cursor.Validate();
            ValidateBinding(cursor, pane);
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

    private static void ValidateBinding(PaneCursor cursor, Pane pane)
    {
        ServerGeneration generation = pane.Generation;
        string endpoint = pane.Server.Connection?.GetEndpointFingerprint()
            ?? throw InvalidCursor();
        if (!string.Equals(cursor.PaneId, pane.Id.ToString(), StringComparison.Ordinal)
            || cursor.ServerProcessId != generation.ProcessId
            || cursor.ServerStartTime != generation.StartTime
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(cursor.EndpointFingerprint),
                Encoding.ASCII.GetBytes(endpoint)))
        {
            throw new McpException(
                "That capture_since cursor belongs to a different pane or tmux server. "
                + "Call capture_since without a cursor to start again here.");
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

    private static McpException InvalidCursor() => new(
        "That capture_since cursor is invalid or is not one this server issued. "
        + "Omit it to start from what is on screen now.");
}

/// <summary>Serializes cursors without reflection.</summary>
[JsonSerializable(typeof(PaneCursor))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal sealed partial class TailCursorJson : JsonSerializerContext;
