using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibTmux.Mcp;

namespace LibTmux.UnitTests.Mcp;

/// <summary>Holds every tool name in the MCP-facing READMEs to a link to its page.</summary>
/// <remarks>
/// A tool name is what a reader searches for next, and nuget.org shows the same
/// Markdown without the repository around it, so each name is an absolute link.
/// The tool names come from the server's own manifest, so a renamed or removed
/// tool fails here rather than leaving a dead link behind.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class ReadmeToolLinkTests
{
    private const string Pages = "https://libtmux.org/en/csharp/latest/mcp/tools/";

    private static readonly Regex Token = new(
        @"(?<link>\[(?<text>[^\]]*)\]\((?<target>[^)]*)\))|`(?<name>[a-z][a-z_]*)`",
        RegexOptions.Compiled);

    public static TheoryData<string> Documents() =>
        new() { "README.md", "src/LibTmux.Mcp/README.md", "docs/mcp/README.md" };

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_tool_name_links_its_page(string document)
    {
        string markdown = File.ReadAllText(Path.Combine(RepositoryRoot(), document));

        string[] defects = [.. Defects(markdown, ToolNames())];

        Assert.True(defects.Length == 0, $"{document}: {string.Join("; ", defects)}");
    }

    [Fact]
    public void The_manifest_names_are_the_page_names()
    {
        // The pages are named for the tool, so a tool the manifest renames
        // needs a new link rather than a guess.
        Assert.All(ToolNames(), name => Assert.Matches("^[a-z]+(_[a-z]+)*$", name));
        Assert.Contains("run_shell_command", ToolNames());
    }

    // A check that cannot fail proves nothing. Each of these is a mistake the
    // check is there to catch.
    [Fact]
    public void A_bare_tool_name_is_reported()
    {
        string[] defects = [.. Defects("Use `capture_pane` here.", ToolNames())];

        Assert.Equal(["`capture_pane` is not a link"], defects);
    }

    [Fact]
    public void A_link_to_another_tools_page_is_reported()
    {
        string markdown = $"Use [`capture_pane`]({Pages}send_keys/) here.";

        string defect = Assert.Single(Defects(markdown, ToolNames()));

        Assert.Contains("capture_pane", defect, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_to_a_tool_that_does_not_exist_is_reported()
    {
        string markdown = $"Use [`capture_panes`]({Pages}capture_panes/) here.";

        string defect = Assert.Single(Defects(markdown, ToolNames()));

        Assert.Contains("capture_panes", defect, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tool_index_is_not_a_tool_name()
    {
        string markdown = $"See [every tool]({Pages}) and [`capture_pane`]({Pages}capture_pane/).";

        Assert.Empty(Defects(markdown, ToolNames()));
    }

    [Fact]
    public void A_relative_tool_link_is_reported()
    {
        string markdown = "Use [`capture_pane`](tools.md#capture_pane) here.";

        Assert.Single(Defects(markdown, ToolNames()));
    }

    [Fact]
    public void Code_blocks_and_headings_are_left_alone()
    {
        string markdown = "## `capture_pane`\n\n```console\n$ run `capture_pane`\n```\n\n"
            + $"[`capture_pane`]({Pages}capture_pane/) and `LIBTMUX_TOOLS`.\n";

        Assert.Empty(Defects(markdown, ToolNames()));
    }

    /// <summary>Returns what is wrong with the tool names in a document.</summary>
    private static IEnumerable<string> Defects(string markdown, HashSet<string> tools)
    {
        bool fenced = false;
        foreach (string line in markdown.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced || line.StartsWith('#') || line.StartsWith("<!--", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in Token.Matches(line))
            {
                if (match.Groups["name"].Success)
                {
                    if (tools.Contains(match.Groups["name"].Value))
                    {
                        yield return $"`{match.Groups["name"].Value}` is not a link";
                    }

                    continue;
                }

                string target = match.Groups["target"].Value;
                string text = match.Groups["text"].Value.Trim('`');
                if (target.StartsWith(Pages, StringComparison.Ordinal))
                {
                    string page = target[Pages.Length..].TrimEnd('/');
                    if (page.Length == 0)
                    {
                        continue;
                    }

                    if (!tools.Contains(page))
                    {
                        yield return $"{target} is not a tool page";
                    }
                    else if (text != page)
                    {
                        yield return $"the link for `{text}` goes to the page for {page}";
                    }
                }
                else if (tools.Contains(text))
                {
                    yield return $"the link for `{text}` goes to {target}, not its page";
                }
            }
        }
    }

    private static HashSet<string> ToolNames() =>
        [.. CapabilityRegistry.Manifest.Select(tool => tool.Name)];

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
}
