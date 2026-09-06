using System.Text;
using System.Text.Json.Nodes;

namespace LibTmux.McpSwap.Tests;

public sealed class ConfigCodecTests
{
    private static readonly ServerSpec Local = new(
        "/tmp/libtmux-mcp",
        ["--stdio"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = "/opt/dotnet",
        });

    [Theory]
    [InlineData("cursor")]
    [InlineData("gemini")]
    [InlineData("agy")]
    public void JsonRoundTripPreservesUnrelatedServersAndNewlineConvention(string name)
    {
        using TestPaths paths = new();
        ClientInfo client = ClientCatalog.Create(paths.Home, paths.ConfigHome)[name];
        byte[] original = Encoding.UTF8.GetBytes(
            "{\n  \"theme\": \"dark\",\n  \"mcpServers\": {\n    \"other\": {\"command\": \"keep\", \"args\": []}\n  }\n}");

        ConfigEdit edit = ConfigCodec.Set(client, original, "tmux", Local, paths.Root, ConfigScope.User);

        Assert.Equal(ConfigAction.Added, edit.Action);
        Assert.False(edit.Bytes.AsSpan().EndsWith("\n"u8));
        JsonNode root = JsonNode.Parse(edit.Bytes)!;
        Assert.Equal("keep", root["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        Assert.Equal(Local, ConfigCodec.Read(client, edit.Bytes, "tmux", paths.Root, ConfigScope.User));
    }

    [Fact]
    public void ClaudeScopesAreIndependentAndUseClaudeEntryShape()
    {
        using TestPaths paths = new();
        ClientInfo client = ClientCatalog.Create(paths.Home, paths.ConfigHome)["claude"];
        byte[] original = "{}"u8.ToArray();

        ConfigEdit user = ConfigCodec.Set(client, original, "tmux", Local, paths.Root, ConfigScope.User);
        ServerSpec projectSpec = Local.WithCommand("/tmp/project-mcp");
        ConfigEdit project = ConfigCodec.Set(
            client,
            user.Bytes,
            "tmux",
            projectSpec,
            paths.Root,
            ConfigScope.Project);

        Assert.Equal(Local, ConfigCodec.Read(client, project.Bytes, "tmux", paths.Root, ConfigScope.User));
        Assert.Equal(projectSpec, ConfigCodec.Read(client, project.Bytes, "tmux", paths.Root, ConfigScope.Project));
        JsonNode root = JsonNode.Parse(project.Bytes)!;
        JsonNode userEntry = root["mcpServers"]!["tmux"]!;
        Assert.Equal("stdio", userEntry["type"]!.GetValue<string>());
        Assert.NotNull(userEntry["env"]);
    }

    [Fact]
    public void JsoncSetPreservesCommentsTrailingCommasAndEntryComments()
    {
        using TestPaths paths = new();
        ClientInfo client = ClientCatalog.Create(paths.Home, paths.ConfigHome)["opencode"];
        byte[] original = Encoding.UTF8.GetBytes(
            "{\n  // global stays\n  \"mcp\": {\n    \"tmux\": {\n      // command stays beside its field\n      \"type\": \"local\",\n      \"command\": [\"old\"],\n      \"environment\": {\"KEEP\": \"yes\"},\n    },\n  },\n}\n");

        ConfigEdit edit = ConfigCodec.Set(client, original, "tmux", Local, paths.Root, ConfigScope.User);

        string text = Encoding.UTF8.GetString(edit.Bytes);
        Assert.Contains("// global stays", text, StringComparison.Ordinal);
        Assert.Contains("// command stays beside its field", text, StringComparison.Ordinal);
        Assert.Contains("\"KEEP\": \"yes\"", text, StringComparison.Ordinal);
        ServerSpec expected = Local.WithEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["KEEP"] = "yes",
                ["DOTNET_ROOT"] = "/opt/dotnet",
            });
        Assert.Equal(expected, ConfigCodec.Read(client, edit.Bytes, "tmux", paths.Root, ConfigScope.User));
    }

    [Fact]
    public void TomlSetPreservesCommentsUnknownFieldsAndQuotedEnvironment()
    {
        using TestPaths paths = new();
        ClientInfo client = ClientCatalog.Create(paths.Home, paths.ConfigHome)["codex"];
        byte[] original = Encoding.UTF8.GetBytes(
            "# global stays\nmodel = \"gpt\"\n\n[mcp_servers.\"tmux\"]\n# target stays\ncommand = \"old\" # inline stays\nargs = [\"serve\"]\ntimeout_sec = 30\n\n[mcp_servers.\"tmux\".env]\n\"KEEP-ME\" = \"yes\"\n\n[mcp_servers.other]\ncommand = \"other\"\n");

        ConfigEdit edit = ConfigCodec.Set(client, original, "tmux", Local, paths.Root, ConfigScope.User);

        string text = Encoding.UTF8.GetString(edit.Bytes);
        Assert.Contains("# global stays", text, StringComparison.Ordinal);
        Assert.Contains("# target stays", text, StringComparison.Ordinal);
        Assert.Contains("# inline stays", text, StringComparison.Ordinal);
        Assert.Contains("timeout_sec = 30", text, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.other]", text, StringComparison.Ordinal);
        ServerSpec expected = Local.WithEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["KEEP-ME"] = "yes",
                ["DOTNET_ROOT"] = "/opt/dotnet",
            });
        Assert.Equal(expected, ConfigCodec.Read(client, edit.Bytes, "tmux", paths.Root, ConfigScope.User));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("agy")]
    [InlineData("cursor")]
    [InlineData("gemini")]
    [InlineData("grok")]
    [InlineData("opencode")]
    [InlineData("pi")]
    [InlineData("codex")]
    public void MalformedUtf8IsRejectedBeforeAnyEdit(string name)
    {
        using TestPaths paths = new();
        ClientInfo client = ClientCatalog.Create(paths.Home, paths.ConfigHome)[name];
        byte[] malformed = [0x7b, 0x22, 0x78, 0x22, 0x3a, 0xff, 0x7d];

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => ConfigCodec.Set(client, malformed, "tmux", Local, paths.Root, ConfigScope.User));

        Assert.Contains("UTF-8", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TomlDeletePreservesInterleavedTablesAndArrayTables()
    {
        using TestPaths paths = new();
        ClientInfo client = ClientCatalog.Create(paths.Home, paths.ConfigHome)["codex"];
        byte[] original = Encoding.UTF8.GetBytes(
            "[mcp_servers.tmux]\ncommand = \"old\"\nargs = []\n\n"
            + "[[plugins]]\nname = \"keep-array\"\n\n"
            + "[mcp_servers.other]\ncommand = \"keep\"\n\n"
            + "[mcp_servers.tmux.env]\nKEEP = \"yes\"\n\n"
            + "[tail]\nvalue = \"keep-tail\"\n");

        ConfigEdit edit = ConfigCodec.Delete(
            client,
            original,
            "tmux",
            paths.Root,
            ConfigScope.User);

        string text = Encoding.UTF8.GetString(edit.Bytes);
        Assert.DoesNotContain("mcp_servers.tmux", text, StringComparison.Ordinal);
        Assert.Contains("[[plugins]]\nname = \"keep-array\"", text, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.other]\ncommand = \"keep\"", text, StringComparison.Ordinal);
        Assert.Contains("[tail]\nvalue = \"keep-tail\"", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mcp_servers.tmux = { command = \"old\" }\n")]
    [InlineData("[mcp_servers]\ntmux = { command = \"old\" }\n")]
    public void TomlSetRejectsInlineTableCollisionsInsteadOfEmittingInvalidToml(string body)
    {
        using TestPaths paths = new();
        ClientInfo client = ClientCatalog.Create(paths.Home, paths.ConfigHome)["codex"];

        Assert.Throws<InvalidDataException>(() => ConfigCodec.Set(
            client,
            Encoding.UTF8.GetBytes(body),
            "tmux",
            Local,
            paths.Root,
            ConfigScope.User));
    }

    [Fact]
    public void JsoncInsertionPreservesCommentOnlyObjectsAndNoFinalNewline()
    {
        using TestPaths paths = new();
        ClientInfo client = ClientCatalog.Create(paths.Home, paths.ConfigHome)["opencode"];
        byte[] original = Encoding.UTF8.GetBytes(
            "{\n  // root rationale\n  \"mcp\": {\n    // no servers yet\n  }\n}");

        ConfigEdit edit = ConfigCodec.Set(
            client,
            original,
            "tmux",
            Local,
            paths.Root,
            ConfigScope.User);

        string text = Encoding.UTF8.GetString(edit.Bytes);
        Assert.Contains("// root rationale", text, StringComparison.Ordinal);
        Assert.Contains("// no servers yet", text, StringComparison.Ordinal);
        Assert.False(text.EndsWith('\n'));
        Assert.Equal(Local, ConfigCodec.Read(client, edit.Bytes, "tmux", paths.Root, ConfigScope.User));
    }

    [Theory]
    [InlineData("back\\slash")]
    [InlineData("quo\"te")]
    [InlineData("unicode-é")]
    public void JsoncInsertionEscapesArbitraryServerNames(string server)
    {
        using TestPaths paths = new();
        ClientInfo client = ClientCatalog.Create(paths.Home, paths.ConfigHome)["opencode"];

        ConfigEdit edit = ConfigCodec.Set(
            client,
            "{\n  \"mcp\": {}\n}\n"u8.ToArray(),
            server,
            Local,
            paths.Root,
            ConfigScope.User);

        Assert.Equal(Local, ConfigCodec.Read(client, edit.Bytes, server, paths.Root, ConfigScope.User));
    }

    [Fact]
    public void MalformedTomlOutsideTheTargetTableBlocksEveryEdit()
    {
        using TestPaths paths = new();
        ClientInfo client = ClientCatalog.Create(paths.Home, paths.ConfigHome)["codex"];
        byte[] malformed = Encoding.UTF8.GetBytes(
            "[unrelated]\nvalue = \"unterminated\n\n"
            + "[mcp_servers.tmux]\ncommand = \"old\"\nargs = []\n");

        Assert.Throws<InvalidDataException>(() => ConfigCodec.Set(
            client,
            malformed,
            "tmux",
            Local,
            paths.Root,
            ConfigScope.User));
    }

    [Theory]
    [InlineData("cursor", "mcpServers")]
    [InlineData("opencode", "mcp")]
    public void DuplicateJsonContainersAreRejectedBeforeEditing(string clientName, string container)
    {
        using TestPaths paths = new();
        ClientInfo client = ClientCatalog.Create(paths.Home, paths.ConfigHome)[clientName];
        byte[] duplicate = Encoding.UTF8.GetBytes($"{{\"{container}\":{{}},\"{container}\":{{}}}}");

        Assert.Throws<InvalidDataException>(() => ConfigCodec.Set(
            client,
            duplicate,
            "tmux",
            Local,
            paths.Root,
            ConfigScope.User));
    }
}
