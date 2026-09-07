using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibTmux.McpSwap;

internal sealed class RecoveryLedger
{
    [JsonPropertyName("next_sequence")]
    public long NextSequence { get; set; }

    [JsonPropertyName("entries")]
    public SortedDictionary<string, RecoveryEntry> Entries { get; set; } =
        new(StringComparer.Ordinal);
}

internal sealed class RecoveryEntry
{
    [JsonPropertyName("client")]
    public required string Client { get; init; }

    [JsonPropertyName("scope")]
    public required ConfigScope Scope { get; init; }

    [JsonPropertyName("sequence")]
    public required long Sequence { get; init; }

    [JsonPropertyName("server")]
    public required string Server { get; init; }

    [JsonPropertyName("action")]
    public required ConfigAction Action { get; init; }

    [JsonPropertyName("repository")]
    public required string Repository { get; init; }

    [JsonPropertyName("swapped_at")]
    public required string SwappedAt { get; init; }

    [JsonPropertyName("config_path")]
    public required string ConfigPath { get; init; }

    [JsonPropertyName("target_path")]
    public required string TargetPath { get; init; }

    [JsonPropertyName("config_parent")]
    public required StoredDirectory ConfigParent { get; init; }

    [JsonPropertyName("link_target")]
    public string? LinkTarget { get; init; }

    [JsonPropertyName("link_identity")]
    public StoredIdentity? LinkIdentity { get; init; }

    [JsonPropertyName("backup_path")]
    public required string BackupPath { get; init; }

    [JsonPropertyName("backup_identity")]
    public required StoredIdentity BackupIdentity { get; init; }

    [JsonPropertyName("expected_config")]
    public required StoredIdentity ExpectedConfig { get; set; }

    [JsonPropertyName("route")]
    public required StoredSpec Route { get; init; }
}

internal sealed record StoredDirectory(
    [property: JsonPropertyName("logical")] string Logical,
    [property: JsonPropertyName("physical")] string Physical,
    [property: JsonPropertyName("identity")] NativeIdentity Identity,
    [property: JsonPropertyName("link_target")] string? LinkTarget,
    [property: JsonPropertyName("link_identity")] NativeIdentity? LinkIdentity)
{
    internal static StoredDirectory From(DirectoryBinding directory) => new(
        directory.Logical,
        directory.Physical,
        directory.Identity,
        directory.LinkTarget,
        directory.LinkIdentity);
}

internal sealed record StoredIdentity(
    [property: JsonPropertyName("device")] ulong Device,
    [property: JsonPropertyName("inode")] ulong Inode,
    [property: JsonPropertyName("mode")] uint Mode,
    [property: JsonPropertyName("links")] ulong LinkCount,
    [property: JsonPropertyName("uid")] uint UserId,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("modified_ns")] long ModifiedNanoseconds,
    [property: JsonPropertyName("sha256")] string Digest)
{
    internal static StoredIdentity From(FileSnapshot snapshot) => new(
        snapshot.Identity.Device,
        snapshot.Identity.Inode,
        snapshot.Identity.Mode,
        snapshot.Identity.LinkCount,
        snapshot.Identity.UserId,
        snapshot.Identity.Size,
        snapshot.Identity.ModifiedNanoseconds,
        snapshot.Digest);

    internal bool Matches(FileSnapshot snapshot) =>
        Device == snapshot.Identity.Device
        && Inode == snapshot.Identity.Inode
        && Mode == snapshot.Identity.Mode
        && LinkCount == snapshot.Identity.LinkCount
        && UserId == snapshot.Identity.UserId
        && Size == snapshot.Identity.Size
        && ModifiedNanoseconds == snapshot.Identity.ModifiedNanoseconds
        && string.Equals(Digest, snapshot.Digest, StringComparison.Ordinal);
}

internal sealed class StoredSpec
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("args")]
    public required string[] Arguments { get; init; }

    [JsonPropertyName("env")]
    public required SortedDictionary<string, string> Environment { get; init; }

    internal static StoredSpec From(ServerSpec spec) => new()
    {
        Command = spec.Command,
        Arguments = spec.Arguments.ToArray(),
        Environment = new SortedDictionary<string, string>(
            spec.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal),
    };

    internal ServerSpec ToSpec() => new(Command, Arguments, Environment);
}

internal static class LedgerCodec
{
    internal const int MaximumBytes = 1 << 20;
    private const int Version = 1;
    private const string Implementation = "dotnet";
    private static readonly JsonSerializerOptions Options = CreateOptions(writeIndented: false);
    private static readonly JsonSerializerOptions PrettyOptions = CreateOptions(writeIndented: true);

    internal static RecoveryLedger Read(string path)
    {
        DestinationBinding artifact = NativeFileSystem.CaptureDestination(path, required: true);
        FileSnapshot snapshot = artifact.File!;
        ValidatePrivate(snapshot, "recovery state");
        if (snapshot.Bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException($"recovery state exceeds {MaximumBytes} bytes");
        }

        return Decode(snapshot.Bytes);
    }

    internal static RecoveryLedger Decode(byte[] bytes)
    {
        _ = ConfigCodec.Decode(bytes, "recovery state");
        LedgerEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<LedgerEnvelope>(bytes, Options)
                ?? throw new InvalidDataException("recovery state is empty");
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException("recovery state is not valid JSON", failure);
        }

        if (envelope.Version != Version)
        {
            throw new InvalidDataException($"unsupported recovery state version {envelope.Version}");
        }

        if (!string.Equals(envelope.Implementation, Implementation, StringComparison.Ordinal))
        {
            throw new InvalidDataException("recovery state belongs to another implementation");
        }

        Validate(envelope.Payload);
        string checksum = Checksum(envelope.Payload);
        if (!string.Equals(checksum, envelope.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException("recovery state checksum mismatch");
        }

        return envelope.Payload;
    }

    internal static byte[] Encode(RecoveryLedger ledger)
    {
        Validate(ledger);
        LedgerEnvelope envelope = new()
        {
            Version = Version,
            Implementation = Implementation,
            Checksum = Checksum(ledger),
            Payload = ledger,
        };
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(envelope, PrettyOptions);
        byte[] bytes = [.. body, (byte)'\n'];
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException($"recovery state exceeds {MaximumBytes} bytes");
        }

        return bytes;
    }

    internal static void ValidatePrivate(FileSnapshot snapshot, string subject)
    {
        if (!snapshot.Identity.IsRegular
            || snapshot.Identity.Permissions != 0x180
            || snapshot.Identity.LinkCount != 1
            || (!OperatingSystem.IsWindows()
                && snapshot.Identity.UserId != NativeFileSystem.GetUserId()))
        {
            throw new InvalidDataException($"{subject} must be an owned 0600 regular file with one link");
        }
    }

    private static void Validate(RecoveryLedger ledger)
    {
        if (ledger.NextSequence < 0 || ledger.Entries is null)
        {
            throw new InvalidDataException("recovery ledger shape is invalid");
        }

        HashSet<long> sequences = [];
        foreach ((string key, RecoveryEntry entry) in ledger.Entries)
        {
            if (entry is null)
            {
                throw new InvalidDataException($"recovery entry {key} is null");
            }

            string expected = StateKey(entry.Client, entry.Scope);
            if (!string.Equals(key, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"noncanonical recovery key {key}");
            }

            if (!ClientNames.Contains(entry.Client, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"unknown recovery client {entry.Client}");
            }

            if (entry.Scope is not ConfigScope.User and not ConfigScope.Project)
            {
                throw new InvalidDataException("unknown recovery scope");
            }

            if (entry.Scope == ConfigScope.Project
                && !string.Equals(entry.Client, "claude", StringComparison.Ordinal))
            {
                throw new InvalidDataException("only Claude recovery may use project scope");
            }

            if (string.IsNullOrWhiteSpace(entry.Server)
                || string.IsNullOrWhiteSpace(entry.Repository)
                || string.IsNullOrWhiteSpace(entry.ConfigPath)
                || string.IsNullOrWhiteSpace(entry.TargetPath)
                || string.IsNullOrWhiteSpace(entry.BackupPath)
                || !Path.IsPathFullyQualified(entry.ConfigPath)
                || !Path.IsPathFullyQualified(entry.TargetPath)
                || !Path.IsPathFullyQualified(entry.BackupPath)
                || !Path.IsPathFullyQualified(entry.Repository))
            {
                throw new InvalidDataException("recovery paths must be absolute");
            }

            if (entry.Sequence < 0
                || entry.Sequence >= ledger.NextSequence
                || !sequences.Add(entry.Sequence))
            {
                throw new InvalidDataException("recovery sequence is invalid or duplicated");
            }

            if (entry.SwappedAt.Length != 14
                || entry.SwappedAt.Any(character => !char.IsAsciiDigit(character)))
            {
                throw new InvalidDataException("recovery swapped_at must be a 14-digit timestamp");
            }

            if (entry.Action is not ConfigAction.Added and not ConfigAction.Replaced)
            {
                throw new InvalidDataException("recovery action must be added or replaced");
            }

            ValidateDirectory(entry.ConfigParent);
            ValidateIdentity(entry.BackupIdentity, "backup", expectSymlink: false);
            ValidateIdentity(entry.ExpectedConfig, "config", expectSymlink: false);
            if (entry.LinkIdentity is not null)
            {
                ValidateIdentity(entry.LinkIdentity, "config symlink", expectSymlink: true);
                if (entry.LinkTarget is null)
                {
                    throw new InvalidDataException("recovery symlink target is missing");
                }
            }
            else if (entry.LinkTarget is not null)
            {
                throw new InvalidDataException("recovery symlink identity is missing");
            }

            if (entry.Route is null
                || string.IsNullOrEmpty(entry.Route.Command)
                || entry.Route.Arguments is null
                || entry.Route.Arguments.Any(value => value is null)
                || entry.Route.Environment is null
                || entry.Route.Environment.Any(pair => pair.Key is null || pair.Value is null))
            {
                throw new InvalidDataException("recovery route is invalid");
            }
        }
    }

    private static void ValidateDirectory(StoredDirectory directory)
    {
        if (directory is null
            || directory.Identity is null
            || !Path.IsPathFullyQualified(directory.Logical)
            || !Path.IsPathFullyQualified(directory.Physical)
            || !directory.Identity.IsDirectory)
        {
            throw new InvalidDataException("recovery config parent is invalid");
        }

        if (directory.LinkIdentity is null != (directory.LinkTarget is null))
        {
            throw new InvalidDataException("recovery config parent symlink is incomplete");
        }

        if (directory.LinkIdentity is not null && !directory.LinkIdentity.IsSymbolicLink)
        {
            throw new InvalidDataException("recovery config parent link identity is invalid");
        }
    }

    private static void ValidateIdentity(
        StoredIdentity identity,
        string subject,
        bool expectSymlink)
    {
        if (identity is null)
        {
            throw new InvalidDataException($"recovery {subject} identity is missing");
        }

        bool correctType = expectSymlink
            ? (identity.Mode & 0xF000U) == 0xA000U
            : (identity.Mode & 0xF000U) == 0x8000U;
        bool digestValid = expectSymlink
            ? identity.Digest.Length == 0
            : identity.Digest.Length == 64
                && identity.Digest.All(Uri.IsHexDigit);
        if (!correctType
            || identity.LinkCount < 1
            || identity.Size < 0
            || !digestValid)
        {
            throw new InvalidDataException($"recovery {subject} identity is invalid");
        }
    }

    internal static string StateKey(string client, ConfigScope scope) =>
        $"{client}:{scope.ToString().ToLowerInvariant()}";

    private static string Checksum(RecoveryLedger ledger) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ledger, Options)));

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = new()
        {
            AllowTrailingCommas = false,
            AllowDuplicateProperties = false,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = writeIndented,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    private static readonly string[] ClientNames =
    [
        "claude",
        "codex",
        "cursor",
        "gemini",
        "grok",
        "agy",
        "opencode",
        "pi",
    ];

    private sealed class LedgerEnvelope
    {
        [JsonPropertyName("version")]
        public required int Version { get; init; }

        [JsonPropertyName("checksum")]
        public required string Checksum { get; init; }

        [JsonPropertyName("implementation")]
        public required string Implementation { get; init; }

        [JsonPropertyName("payload")]
        public required RecoveryLedger Payload { get; init; }
    }
}
