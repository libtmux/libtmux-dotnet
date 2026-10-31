using System.Collections;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace LibTmux.Workspace.Cli.Tests;

// The package READMEs are what a reader pastes from. A command they show that
// the tool no longer parses is a broken front door, and a quickstart whose
// workspace builds something else is a quiet one.
[UnsupportedOSPlatform("windows")]
public sealed class ReadmeCommandTests : IAsyncDisposable
{
    private static readonly Regex Command = new(
        @"^\$ tmux-workspace (?<args>(?:.*\\\n)*.*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex YamlFence = new(
        @"^```yaml\n(?<body>.*?)^```",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "cli-readme-" + Guid.NewGuid().ToString("N")[..8]);

    public ReadmeCommandTests() => Directory.CreateDirectory(_root);

    public static TheoryData<string> Documents()
    {
        string root = RepositoryRoot();
        TheoryData<string> documents = new() { "README.md", "docs/mcp/README.md" };
        foreach (string project in Directory.GetDirectories(Path.Combine(root, "src")))
        {
            string readme = Path.Combine(project, "README.md");
            if (File.Exists(readme))
            {
                documents.Add(Path.GetRelativePath(root, readme));
            }
        }

        return documents;
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_documented_command_parses(string document)
    {
        string text = File.ReadAllText(Path.Combine(RepositoryRoot(), document));
        CommandLine graph = new();

        foreach (string[] arguments in Commands(text))
        {
            Assert.True(
                graph.Parse(arguments).Errors.Count == 0,
                $"{document}: tmux-workspace {string.Join(' ', arguments)}");
        }
    }

    // A check that cannot fail proves nothing. These are the mistakes it is
    // there to catch, written the way a README would carry them.
    [Theory]
    [InlineData("$ tmux-workspace load --no-such-flag myproject")]
    [InlineData("$ tmux-workspace lode myproject")]
    [InlineData("$ tmux-workspace load -d -L\n")]
    [InlineData("$ tmux-workspace ls --treee")]
    public void A_stale_command_is_reported(string markdown)
    {
        CommandLine graph = new();
        string[] arguments = Assert.Single(Commands(markdown));

        Assert.True(graph.Parse(arguments).Errors.Count > 0, markdown);
    }

    [Fact]
    public void Continued_lines_and_trailing_pipes_are_read_as_one_command()
    {
        string[] arguments = Assert.Single(Commands(
            "$ tmux-workspace import tmuxinator blog.yml \\\n    --save-to blog.yaml\n"));

        Assert.Equal(["import", "tmuxinator", "blog.yml", "--save-to", "blog.yaml"], arguments);
        Assert.Equal(["ls", "--json"], Assert.Single(Commands("$ tmux-workspace ls --json | jq -r .\n")));
    }

    [Fact]
    public void The_root_readme_shows_the_private_socket_load()
    {
        string text = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));

        Assert.Contains(
            Commands(text),
            arguments => arguments.SequenceEqual(["load", "-d", "-L", "demo", "myproject"]));
    }

    [Fact]
    public async Task The_quickstart_workspace_builds_what_the_readme_describes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string repository = RepositoryRoot();
        string packageReadme = File.ReadAllText(
            Path.Combine(repository, "src", "LibTmux.Workspace.Cli", "README.md"));
        string yaml = YamlFence.Match(packageReadme).Groups["body"].Value;
        Assert.Contains("session_name: myproject", yaml, StringComparison.Ordinal);

        // The README saves the file as ~/.tmuxp/myproject.yaml.
        string home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".tmuxp"));
        Directory.CreateDirectory(Path.Combine(home, "code", "myproject"));
        await File.WriteAllTextAsync(Path.Combine(home, ".tmuxp", "myproject.yaml"), yaml, token);

        string[] load = Commands(File.ReadAllText(Path.Combine(repository, "README.md")))
            .Single(arguments => arguments.SequenceEqual(["load", "-d", "-L", "demo", "myproject"]));
        string socketRoot = Path.Combine(_root, "tmux");
        Directory.CreateDirectory(socketRoot);
        Dictionary<string, string?> environment = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.Ordinal);
        environment["HOME"] = home;
        environment["TMUX_TMPDIR"] = socketRoot;
        environment["NO_COLOR"] = "1";
        foreach (string inherited in new[] { "TMUX", "TMUX_PANE", "TMUXP_CONFIGDIR", "XDG_CONFIG_HOME" })
        {
            environment.Remove(inherited);
        }

        Server server = Server.Open(new ServerConnectionOptions
        {
            TmuxBinaryPath = Environment.GetEnvironmentVariable("LIBTMUX_TMUX") ?? "tmux",
            SocketName = load[3],
            ConfigurationFile = "/dev/null",
            ChildEnvironment = new Dictionary<string, string?> { ["TMUX_TMPDIR"] = socketRoot },
        });
        try
        {
            using StringWriter output = new();
            using StringWriter error = new();
            int code = await CliRunner.RunAsync(
                [.. load, "-f", "/dev/null"], output, error, _root, environment, token);
            Assert.True(code == 0, error.ToString());

            Session session = Assert.Single(await server.GetSessionsAsync(token));
            Assert.Equal("myproject", session.Name);
            IReadOnlyList<Window> windows = await session.GetWindowsAsync(token);
            Assert.Equal(["editor", "server"], windows.Select(window => window.Name));
            int[] counts = await Task.WhenAll(
                windows.Select(async window => (await window.GetPanesAsync(token)).Count));
            Assert.Equal(2, counts[0]);
            Assert.Equal(1, counts[1]);
        }
        finally
        {
            await server.KillAsync(CancellationToken.None);
        }
    }

    // The killed server's pane shells write .bash_history and the programs the
    // quick start runs write under .local while they exit, so an entry can
    // appear in the private HOME after the delete has emptied it.
    public async ValueTask DisposeAsync()
    {
        for (int attempt = 1; Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(100, CancellationToken.None);
            }
        }
    }

    private static string[][] Commands(string markdown) =>
    [
        .. Command.Matches(markdown)
            .Select(match => match.Groups["args"].Value
                .Replace("\\\n", " ", StringComparison.Ordinal)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .TakeWhile(word => word is not "|" and not ">")
                .ToArray()),
    ];

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LibTmux.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("The repository root was not found above the test assembly.");
    }
}
