using System.Text;

namespace LibTmux.McpSwap.Tests;

public sealed class RecoveryLedgerTests
{
    [Fact]
    public void StrictLedgerRoundTripsItsRecoveryContract()
    {
        RecoveryLedger ledger = Example();

        RecoveryLedger decoded = LedgerCodec.Decode(LedgerCodec.Encode(ledger));

        RecoveryEntry entry = Assert.Single(decoded.Entries).Value;
        Assert.Equal(ConfigAction.Replaced, entry.Action);
        Assert.Equal("20260903010203", entry.SwappedAt);
        Assert.Equal("tmux", entry.Server);
        Assert.Equal(1, decoded.NextSequence);
    }

    [Fact]
    public void MalformedUtf8IsRejectedBeforeJsonParsing()
    {
        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => LedgerCodec.Decode([0x7b, 0xff, 0x7d]));

        Assert.Contains("UTF-8", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateAndUnknownEnvelopePropertiesAreRejected()
    {
        string valid = Encoding.UTF8.GetString(LedgerCodec.Encode(Example()));
        string duplicate = valid.Replace("{", "{\n  \"version\": 1,", StringComparison.Ordinal);
        string unknown = valid.Replace("{", "{\n  \"unknown\": true,", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => LedgerCodec.Decode(Encoding.UTF8.GetBytes(duplicate)));
        Assert.Throws<InvalidDataException>(() => LedgerCodec.Decode(Encoding.UTF8.GetBytes(unknown)));
    }

    [Fact]
    public void ChecksumCoversEveryPayloadField()
    {
        string valid = Encoding.UTF8.GetString(LedgerCodec.Encode(Example()));
        string tampered = valid.Replace("\"tmux\"", "\"other\"", StringComparison.Ordinal);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => LedgerCodec.Decode(Encoding.UTF8.GetBytes(tampered)));

        Assert.Contains("checksum", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyClaudeMayRecordProjectScope()
    {
        RecoveryLedger ledger = Example("cursor", ConfigScope.Project);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => LedgerCodec.Encode(ledger));

        Assert.Contains("project scope", failure.Message, StringComparison.Ordinal);
    }

    private static RecoveryLedger Example(
        string client = "cursor",
        ConfigScope scope = ConfigScope.User)
    {
        NativeIdentity directory = new(1, 2, 0x41C0, 1, NativeFileSystem.GetUserId(), 0, 1);
        StoredIdentity file = new(
            1,
            3,
            0x8180,
            1,
            NativeFileSystem.GetUserId(),
            2,
            1,
            new string('a', 64));
        RecoveryLedger ledger = new() { NextSequence = 1 };
        ledger.Entries[LedgerCodec.StateKey(client, scope)] = new()
        {
            Client = client,
            Scope = scope,
            Sequence = 0,
            Server = "tmux",
            Action = ConfigAction.Replaced,
            Repository = Path.GetFullPath("repo"),
            SwappedAt = "20260903010203",
            ConfigPath = Path.GetFullPath("config.json"),
            TargetPath = Path.GetFullPath("target.json"),
            ConfigParent = new(
                Path.GetFullPath("."),
                Path.GetFullPath("."),
                directory,
                null,
                null),
            LinkTarget = null,
            LinkIdentity = null,
            BackupPath = Path.GetFullPath("backup.json"),
            BackupIdentity = file,
            ExpectedConfig = file,
            Route = StoredSpec.From(new ServerSpec("server")),
        };
        return ledger;
    }
}
