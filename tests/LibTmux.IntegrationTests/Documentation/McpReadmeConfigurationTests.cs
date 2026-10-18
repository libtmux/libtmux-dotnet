using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibTmux.Engineering;
using LibTmux.IntegrationTests.Transport;

namespace LibTmux.IntegrationTests.Documentation;

/// <summary>Holds the MCP server configuration the documents show to what the server does.</summary>
/// <remarks>
/// A client is configured from a README, by pasting a JSON object or a
/// <c>claude mcp add</c> command. Each setting a document shows is handed to
/// the real executable, and its startup report has to say the setting took
/// effect, so a variable the server stopped reading, or a value it now
/// refuses, fails here rather than in a user's client log.
/// </remarks>
[Collection("tmux control clients")]
[UnsupportedOSPlatform("windows")]
public sealed class McpReadmeConfigurationTests
{
    private const string Handshake =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"readme","version":"1"}}}""";

    private static readonly string[] Documents =
    [
        "README.md",
        "src/LibTmux/README.md",
        "src/LibTmux.Mcp/README.md",
        "docs/mcp/README.md",
    ];

    private static readonly Regex JsonFence = new(
        @"^```json\r?\n(?<body>.*?)^```",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AddCommand = new(
        @"^\$ claude mcp add (?<args>(?:.*\\\n)*.*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ProbeCommand = new(
        @"^\$ \{ printf '%s\\n'(?<body>(?:.*\\\n)*.*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex QuotedObject = new(
        @"'(?<json>\{[^']*\})'",
        RegexOptions.Compiled);

    private static readonly Regex Assignment = new(
        @"\| (?<settings>(?:[A-Z][A-Z_]*=\S+ )*)libtmux-mcp",
        RegexOptions.Compiled);

    [UnixFact]
    public async Task Documented_settings_take_effect()
    {
        string root = RepositoryRoot();
        List<IReadOnlyDictionary<string, string>> configurations = [];
        foreach (string document in Documents)
        {
            foreach (IReadOnlyDictionary<string, string> found in Settings(
                File.ReadAllText(Path.Combine(root, document))))
            {
                if (!configurations.Any(known => known.OrderBy(pair => pair.Key)
                        .SequenceEqual(found.OrderBy(pair => pair.Key))))
                {
                    configurations.Add(found);
                }
            }
        }

        // The documents show a bare server, a socket, a toolset selection and
        // an exclusion; fewer means the parser stopped reading them.
        Assert.True(configurations.Count >= 5, $"Only {configurations.Count} configurations were read.");

        string source = McpSource();
        foreach (IReadOnlyDictionary<string, string> settings in configurations)
        {
            foreach (string variable in settings.Keys)
            {
                Assert.True(
                    source.Contains($"\"{variable}\"", StringComparison.Ordinal),
                    $"{variable} is not a name the server reads.");
            }

            Serving served = await ServeAsync(
                settings,
                [Handshake, Notification, Read("tmux://capabilities"), ListTools]);

            string described = string.Join(", ", settings.Select(pair => $"{pair.Key}={pair.Value}"));
            Assert.True(served.ExitCode == 0, $"[{described}] exited {served.ExitCode}: {served.Error}");
            JsonNode report = JsonNode.Parse(
                served.Result(3)["contents"]![0]!["text"]!.GetValue<string>())!;
            string[] listed = [.. served.Result(2)["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>())];
            string[] effective = [.. report["effectiveTools"]!.AsArray().Select(tool => tool!.GetValue<string>())];
            Assert.Equal(effective.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));

            if (settings.TryGetValue("LIBTMUX_SOCKET", out string? socket))
            {
                Assert.Equal($"name:{socket}", report["socket"]!["selector"]!.GetValue<string>());
            }

            if (settings.TryGetValue("LIBTMUX_TOOLSETS", out string? toolsets))
            {
                Assert.Equal(
                    toolsets.Split(',').Order(StringComparer.Ordinal),
                    report["toolsets"]!.AsArray().Select(toolset => toolset!.GetValue<string>()).Order(StringComparer.Ordinal));
            }

            if (settings.TryGetValue("LIBTMUX_EXCLUDE_TOOLS", out string? excluded))
            {
                string[] names = excluded.Split(',');
                Assert.Equal(
                    names.Order(StringComparer.Ordinal),
                    report["excludedTools"]!.AsArray().Select(tool => tool!.GetValue<string>()).Order(StringComparer.Ordinal));
                Assert.Empty(effective.Intersect(names));
            }
        }
    }

    [UnixFact]
    public async Task Documented_probes_are_answered()
    {
        string root = RepositoryRoot();
        string text = File.ReadAllText(Path.Combine(root, "src", "LibTmux.Mcp", "README.md"));
        Match[] probes = [.. ProbeCommand.Matches(text).Cast<Match>()];
        Assert.True(probes.Length >= 2, "The README shows a handshake probe and a tool listing.");

        foreach (Match probe in probes)
        {
            string command = probe.Groups["body"].Value.Replace("\\\n", " ", StringComparison.Ordinal);
            string[] requests = [.. QuotedObject.Matches(command).Select(match => match.Groups["json"].Value)];
            Dictionary<string, string> settings = Assignment.Match(command).Groups["settings"].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(setting => setting.Split('=', 2))
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

            Serving served = await ServeAsync(settings, requests);

            Assert.True(served.ExitCode == 0, $"exited {served.ExitCode}: {served.Error}");
            Assert.Equal("tmux", served.Result(1)["serverInfo"]!["name"]!.GetValue<string>());
            if (requests.Any(request => request.Contains("tools/list", StringComparison.Ordinal)))
            {
                string[] names = [.. served.Result(2)["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>())];
                Assert.NotEmpty(names);
                if (settings.TryGetValue("LIBTMUX_TOOLSETS", out string? toolsets) && toolsets == "inspect")
                {
                    Assert.Contains("list_sessions", names);
                    Assert.DoesNotContain("send_keys", names);
                }
            }
        }
    }

    // A check that cannot fail proves nothing: the server has to refuse a
    // value it does not know, and the harness has to see the refusal.
    [UnixFact]
    public async Task A_setting_the_server_refuses_is_noticed()
    {
        Serving served = await ServeAsync(
            new Dictionary<string, string> { ["LIBTMUX_TOOLSETS"] = "nonsense" },
            [Handshake]);

        Assert.NotEqual(0, served.ExitCode);
        Assert.Contains("unknown toolset", served.Error, StringComparison.Ordinal);
        Assert.Empty(served.Replies);
    }

    [Fact]
    public void A_misspelled_variable_is_not_a_name_the_server_reads()
    {
        string source = McpSource();

        Assert.Contains("\"LIBTMUX_TOOLSETS\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"LIBTMUX_TOOLSET\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_are_read_from_json_and_from_commands()
    {
        string markdown = """
            ```json
            {"mcpServers":{"tmux":{"command":"libtmux-mcp","env":{"LIBTMUX_SOCKET":"a"}},"other":{"command":"dnx"}}}
            ```

            ```console
            $ claude mcp add tmux \
                -e LIBTMUX_TOOLSETS=inspect \
                -e LIBTMUX_SOCKET=b \
                -- libtmux-mcp
            ```
            """;

        IReadOnlyDictionary<string, string>[] found = [.. Settings(markdown)];

        Assert.Equal(2, found.Length);
        Assert.Equal("a", found[0]["LIBTMUX_SOCKET"]);
        Assert.Equal("inspect", found[1]["LIBTMUX_TOOLSETS"]);
        Assert.Equal("b", found[1]["LIBTMUX_SOCKET"]);
    }

    [Theory]
    [InlineData("$ claude mcp add tmux -- libtmux-mcp --verbose")]
    [InlineData("$ claude mcp add tmux -e LIBTMUX_SOCKET -- libtmux-mcp")]
    [InlineData("$ claude mcp add tmux -e LIBTMUX_SOCKET=a libtmux-mcp")]
    public void A_command_the_parser_cannot_read_is_rejected(string markdown) =>
        Assert.ThrowsAny<Exception>(() => Settings(markdown).ToArray());

    [Fact]
    public void A_malformed_configuration_is_rejected()
    {
        string markdown = "```json\n{\"mcpServers\": {\"tmux\": {\"command\": \"libtmux-mcp\", \"env\": {\"LIBTMUX_SOCKET\": 1}}}}\n```\n";

        Assert.ThrowsAny<Exception>(() => Settings(markdown).ToArray());
    }

    private static string Notification => """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private static string ListTools => """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    private static string Read(string uri) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "resources/read",
            ["params"] = new JsonObject { ["uri"] = uri },
        }.ToJsonString();

    /// <summary>Returns every distinct set of environment settings a document configures the server with.</summary>
    private static IEnumerable<IReadOnlyDictionary<string, string>> Settings(string markdown)
    {
        foreach (Match fence in JsonFence.Matches(markdown))
        {
            string body = fence.Groups["body"].Value;
            if (!body.Contains("\"mcpServers\"", StringComparison.Ordinal))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(body);
            foreach (JsonProperty server in document.RootElement.GetProperty("mcpServers").EnumerateObject())
            {
                if (server.Value.GetProperty("command").GetString() != "libtmux-mcp")
                {
                    continue;
                }

                Dictionary<string, string> environment = new(StringComparer.Ordinal);
                if (server.Value.TryGetProperty("env", out JsonElement env))
                {
                    foreach (JsonProperty variable in env.EnumerateObject())
                    {
                        environment[variable.Name] = variable.Value.GetString()
                            ?? throw new FormatException($"{variable.Name} is not a string.");
                    }
                }

                yield return environment;
            }
        }

        foreach (Match command in AddCommand.Matches(markdown))
        {
            string[] words = [.. command.Groups["args"].Value
                .Replace("\\\n", " ", StringComparison.Ordinal)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)];
            Dictionary<string, string> environment = new(StringComparer.Ordinal);
            int index = 1;
            while (index < words.Length && words[index] == "-e")
            {
                string[] pair = words[index + 1].Split('=', 2);
                if (pair.Length != 2)
                {
                    throw new FormatException($"'{words[index + 1]}' is not NAME=value.");
                }

                environment[pair[0]] = pair[1];
                index += 2;
            }

            if (!words.Skip(index).SequenceEqual(["--", "libtmux-mcp"]))
            {
                throw new FormatException($"Unexpected command: {string.Join(' ', words)}");
            }

            yield return environment;
        }
    }

    private static async Task<Serving> ServeAsync(
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyList<string> requests)
    {
        CancellationToken test = TestContext.Current.CancellationToken;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(test);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        CancellationToken token = timeout.Token;

        string root = Path.Combine(WorkspaceSocketRoot.Root, $"mcp-readme-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(root);
        ProcessStartInfo startInfo = new(Path.Combine(AppContext.BaseDirectory, "LibTmux.Mcp"))
        {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string inherited in startInfo.Environment.Keys
                     .Where(name => name.StartsWith("LIBTMUX_", StringComparison.Ordinal)
                         && name != "LIBTMUX_TMUX")
                     .ToArray())
        {
            startInfo.Environment.Remove(inherited);
        }

        foreach (KeyValuePair<string, string> setting in settings)
        {
            startInfo.Environment[setting.Key] = setting.Value;
        }

        startInfo.Environment["TMUX_TMPDIR"] = root;
        startInfo.Environment.Remove("TMUX");
        startInfo.Environment.Remove("TMUX_PANE");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The MCP executable did not start.");
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        List<JsonNode> replies = [];
        try
        {
            process.StandardInput.AutoFlush = true;
            foreach (string request in requests)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync(request.AsMemory(), token);
                }
                catch (IOException)
                {
                    break;
                }
            }

            // Standard input stays open until every request has its reply:
            // closing it first tears the session down mid-answer.
            int expected = requests.Count(request => request.Contains("\"id\"", StringComparison.Ordinal));
            while (replies.Count < expected
                && await process.StandardOutput.ReadLineAsync(token) is string line)
            {
                JsonNode reply = JsonNode.Parse(line)!;
                if (reply["id"] is not null)
                {
                    replies.Add(reply);
                }
            }
        }
        finally
        {
            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        string stderr = await error;
        Directory.Delete(root, recursive: true);
        return new Serving(process.ExitCode, stderr, replies);
    }

    private static string McpSource() => string.Join(
        '\n',
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "LibTmux.Mcp"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LibTmux.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private sealed record Serving(int ExitCode, string Error, IReadOnlyList<JsonNode> Replies)
    {
        public JsonNode Result(int id) =>
            Replies.SingleOrDefault(reply => reply["id"]?.GetValue<int>() == id)?["result"]
            ?? throw new InvalidOperationException($"No result arrived for request {id}. Error output: {Error}");
    }
}
