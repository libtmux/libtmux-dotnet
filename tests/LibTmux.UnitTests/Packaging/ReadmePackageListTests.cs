using System.Text.RegularExpressions;

namespace LibTmux.UnitTests.Packaging;

/// <summary>Holds the package lists in the READMEs to the packages that ship, and each page to a shape a reader can scan.</summary>
/// <remarks>
/// The root README and the core package README each list every package under a
/// heading that links its directory. nuget.org does not rewrite a relative
/// link, so the links are absolute, and an absolute link to this repository's
/// <c>master</c> branch is only as good as the path it names. These tests
/// resolve those paths against the checkout, and they do not reach the network.
/// A page also opens with its quick start and keeps each paragraph and list item
/// short, because the contracts those paragraphs once held live in each
/// package's <c>docs/contracts.md</c>.
/// </remarks>
public sealed class ReadmePackageListTests
{
    private const string Repository = "https://github.com/libtmux/libtmux-dotnet/";

    // Four 80-column lines of rendered text, which is the longest block a reader takes in at once.
    private const int LongestBlock = 240;

    private static readonly Regex RepositoryLink = new(
        @"https://github\.com/libtmux/libtmux-dotnet/(?<kind>tree|blob)/master/(?<path>[^\s)#>""\]]+)",
        RegexOptions.Compiled);

    private static readonly Regex PackageHeading = new(
        @"^#{2,6} \[`?(?<name>[^\]`]+)`?\]\(https://github\.com/libtmux/libtmux-dotnet/tree/master/(?<path>[^)\s]+)\)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex LogoHeader = new(
        @"\A<!-- libtmux-logo -->\r?\n<div align=""center"">\r?\n  <picture>\r?\n"
        + @"    <source srcset=""assets/logo\.svg"" type=""image/svg\+xml"">\r?\n"
        + @"    <img src=""assets/logo\.png"" width=""128"" height=""128"" alt=""libtmux for [^""]+"">\r?\n"
        + @"  </picture>\r?\n</div>\r?\n<!-- /libtmux-logo -->\r?\n\r?\n",
        RegexOptions.Compiled);

    // The pack step removes exactly this, so what is left is what nuget.org renders.
    private static readonly Regex LogoBlock = new(
        @"<!-- libtmux-logo -->.*?<!-- /libtmux-logo -->\r?\n(\r?\n)?",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Heading = new(@"^#{1,3} ", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex RelativeLink = new(
        @"\]\((?<target>(?!https?://|mailto:|#)[^)\s]+)\)|^ {0,3}\[[^\]]+\]:[ \t]*(?<target>(?!https?://|mailto:|#)\S+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex LinkTarget = new(
        @"\]\((?:[^)\s]*)\)|\]\[[^\]]*\]",
        RegexOptions.Compiled);

    private static readonly Regex ListItem = new(@"^\s*(?:[-*]|\d+\.)\s", RegexOptions.Compiled);

    private static readonly Regex NotProse = new(
        @"^(?:\||#|<|\[!|\[[^\]]+\]:\s)",
        RegexOptions.Compiled);

    public static TheoryData<string> Listings() => new() { "README.md", "src/LibTmux/README.md" };

    public static TheoryData<string> Readmes()
    {
        TheoryData<string> readmes = new() { "README.md", "docs/mcp/README.md" };
        foreach (string project in Packages())
        {
            readmes.Add($"src/{project}/README.md");
        }

        return readmes;
    }

    public static TheoryData<string> LogoDocuments()
    {
        TheoryData<string> documents = new() { "README.md" };
        foreach (string project in Packages())
        {
            documents.Add($"src/{project}/README.md");
        }

        return documents;
    }

    [Theory]
    [MemberData(nameof(LogoDocuments))]
    public void Every_readme_opens_with_the_shared_logo_header(string document)
    {
        string root = RepositoryRoot();
        string markdown = File.ReadAllText(Path.Combine(root, document));

        Assert.True(LogoHeader.IsMatch(markdown), $"{document} does not open with the shared logo header.");
        string assets = Path.Combine(Path.GetDirectoryName(Path.Combine(root, document))!, "assets");
        Assert.True(File.Exists(Path.Combine(assets, "logo.svg")), $"{document} has no assets/logo.svg beside it.");
        Assert.True(File.Exists(Path.Combine(assets, "logo.png")), $"{document} has no assets/logo.png beside it.");
    }

    [Fact]
    public void Packing_leaves_no_html_in_any_package_readme()
    {
        foreach (string project in Packages())
        {
            string markdown = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", project, "README.md"));

            string[] html = [.. PackedHtml(markdown)];

            Assert.True(html.Length == 0, $"{project}: packing keeps {string.Join("; ", html)}");
        }
    }

    [Theory]
    [MemberData(nameof(Listings))]
    public void The_package_list_is_complete_and_linked(string document)
    {
        string markdown = File.ReadAllText(Path.Combine(RepositoryRoot(), document));

        string[] defects = [.. Defects(markdown, Packages())];

        Assert.True(defects.Length == 0, $"{document}: {string.Join("; ", defects)}");
    }

    [Fact]
    public void The_root_index_has_a_row_for_every_package()
    {
        string markdown = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));

        string[] defects = [.. IndexDefects(markdown, "## Packages", Packages())];

        Assert.True(defects.Length == 0, string.Join("; ", defects));
    }

    [Fact]
    public void The_core_index_has_a_row_for_every_other_package()
    {
        string markdown = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/LibTmux/README.md"));

        string[] defects = [.. IndexDefects(markdown, "## Related packages", Packages().Except(["LibTmux"]))];

        Assert.True(defects.Length == 0, string.Join("; ", defects));
    }

    [Theory]
    [InlineData("README.md", "## Testing your own code")]
    [InlineData("README.md", "## Workspaces from the command line")]
    [InlineData("README.md", "## An assistant on your terminal")]
    [InlineData("src/LibTmux/README.md", "## Testing your own code")]
    public void The_sections_that_map_the_page_stay(string document, string heading)
    {
        string markdown = File.ReadAllText(Path.Combine(RepositoryRoot(), document));

        Assert.Contains($"\n{heading}\n", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Readmes))]
    public void Repository_links_resolve_in_the_checkout(string document)
    {
        string markdown = File.ReadAllText(Path.Combine(RepositoryRoot(), document));

        string[] missing = [.. Unresolved(markdown, RepositoryRoot())];

        Assert.True(missing.Length == 0, $"{document} links paths that are not in the checkout: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/mcp/README.md")]
    public void Relative_links_resolve_from_the_document(string document)
    {
        string root = RepositoryRoot();
        string directory = Path.GetDirectoryName(Path.Combine(root, document))!;
        string markdown = File.ReadAllText(Path.Combine(root, document));

        string[] missing =
        [
            .. RelativeLink.Matches(markdown)
                .Select(match => match.Groups["target"].Value.Split('#')[0])
                .Where(target => target.Length > 0)
                .Where(target => !File.Exists(Path.Combine(directory, target))
                    && !Directory.Exists(Path.Combine(directory, target))),
        ];

        Assert.True(missing.Length == 0, $"{document} links files that are not there: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(Readmes))]
    public void The_first_section_is_the_quick_start(string document)
    {
        string markdown = File.ReadAllText(Path.Combine(RepositoryRoot(), document));

        Assert.Equal("Quick start", FirstSection(markdown));
    }

    [Theory]
    [MemberData(nameof(LogoDocuments))]
    public void Every_package_readme_says_what_it_leaves_out_and_where_it_stands(string document)
    {
        string markdown = File.ReadAllText(Path.Combine(RepositoryRoot(), document));

        Assert.Contains("\n## What it leaves out\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n## Support and status\n", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Readmes))]
    public void No_paragraph_or_list_item_is_a_wall(string document)
    {
        string markdown = File.ReadAllText(Path.Combine(RepositoryRoot(), document));

        string[] walls = [.. Walls(markdown)];

        Assert.True(walls.Length == 0, $"{document} has blocks over {LongestBlock} rendered characters: {string.Join(" | ", walls)}");
    }

    // A check that cannot fail proves nothing. Each of these is a mistake the
    // list is there to catch.
    [Fact]
    public void A_link_to_a_path_that_is_not_there_is_reported()
    {
        string markdown = $"[x]({Repository}tree/master/src/NoSuchPackage), [y]({Repository}blob/master/README.md), [z]({Repository}blob/master/src)";

        string[] missing = [.. Unresolved(markdown, RepositoryRoot())];

        Assert.Equal(["src/NoSuchPackage", "src"], missing);
    }

    [Fact]
    public void A_package_without_a_heading_is_reported()
    {
        string markdown = Section("LibTmux", "src/LibTmux");

        string defect = Assert.Single(Defects(markdown, ["LibTmux", "LibTmux.Testing"]));

        Assert.Contains("LibTmux.Testing", defect, StringComparison.Ordinal);
    }

    [Fact]
    public void A_heading_that_links_another_directory_is_reported()
    {
        string markdown = Section("LibTmux.Testing", "src/LibTmux");

        string defect = Assert.Single(Defects(markdown, ["LibTmux.Testing"]));

        Assert.Contains("src/LibTmux", defect, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_without_its_package_page_or_reference_is_reported()
    {
        string markdown = $"### [`LibTmux`]({Repository}tree/master/src/LibTmux): the client\n\nNothing else.\n";

        string[] defects = [.. Defects(markdown, ["LibTmux"])];

        Assert.Equal(2, defects.Length);
        Assert.Contains(defects, defect => defect.Contains("NuGet", StringComparison.Ordinal));
        Assert.Contains(defects, defect => defect.Contains("libtmux.org", StringComparison.Ordinal));
    }

    [Fact]
    public void An_index_without_its_heading_is_reported()
    {
        string defect = Assert.Single(IndexDefects("# Title\n\n| Package | Adds |\n|---|---|\n", "## Packages", ["LibTmux"]));

        Assert.Contains("## Packages", defect, StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_missing_a_row_is_reported()
    {
        string markdown = "## Packages\n\n| Package | Adds |\n|---|---|\n"
            + "| **[LibTmux](a)** | the client |\n| [LibTmux.Workspace.Cli](b) | a tool |\n\n## Next\n";

        string defect = Assert.Single(IndexDefects(markdown, "## Packages", ["LibTmux", "LibTmux.Workspace", "LibTmux.Workspace.Cli"]));

        Assert.EndsWith("no row for LibTmux.Workspace", defect, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_in_a_later_section_is_not_the_index()
    {
        string markdown = "## Packages\n\nNo table here.\n\n## Next\n\n| [LibTmux](a) | x |\n";

        Assert.Single(IndexDefects(markdown, "## Packages", ["LibTmux"]));
    }

    [Fact]
    public void A_reference_link_counts_through_its_definition()
    {
        string markdown = $"### [`LibTmux`]({Repository}tree/master/src/LibTmux): the client\n\n"
            + "[NuGet](https://www.nuget.org/packages/LibTmux) and [the API][api].\n\n"
            + "[api]: https://libtmux.org/en/csharp/latest/reference/\n";

        Assert.Empty(Defects(markdown, ["LibTmux"]));
    }

    [Fact]
    public void A_reference_link_with_no_definition_is_not_a_reference()
    {
        string markdown = $"### [`LibTmux`]({Repository}tree/master/src/LibTmux): the client\n\n"
            + "[NuGet](https://www.nuget.org/packages/LibTmux) and [the API][api].\n";

        string defect = Assert.Single(Defects(markdown, ["LibTmux"]));

        Assert.Contains("libtmux.org", defect, StringComparison.Ordinal);
    }

    private const string Header =
        "<!-- libtmux-logo -->\n<div align=\"center\">\n  <picture>\n"
        + "    <source srcset=\"assets/logo.svg\" type=\"image/svg+xml\">\n"
        + "    <img src=\"assets/logo.png\" width=\"128\" height=\"128\" alt=\"libtmux for .NET\">\n"
        + "  </picture>\n</div>\n<!-- /libtmux-logo -->\n\n";

    [Fact]
    public void The_logo_header_is_accepted_and_packs_to_nothing()
    {
        string markdown = Header + "# LibTmux\n";

        Assert.Matches(LogoHeader, markdown);
        Assert.Empty(PackedHtml(markdown));
        Assert.Equal("# LibTmux\n", LogoBlock.Replace(markdown, string.Empty));
    }

    [Theory]
    [InlineData("<!-- libtmux-logo -->\n<p align=\"center\">\n  <picture>\n    <source srcset=\"assets/logo.svg\" type=\"image/svg+xml\">\n    <img src=\"assets/logo.png\" width=\"128\" height=\"128\" alt=\"libtmux for .NET\">\n  </picture>\n</p>\n<!-- /libtmux-logo -->\n\n# T\n")]
    [InlineData("# LibTmux\n\nNo logo here.\n")]
    [InlineData("<!-- libtmux-logo -->\n<div align=\"center\">\n  <picture>\n    <source srcset=\"../assets/logo.svg\" type=\"image/svg+xml\">\n    <img src=\"../assets/logo.png\" width=\"128\" height=\"128\" alt=\"libtmux for .NET\">\n  </picture>\n</div>\n<!-- /libtmux-logo -->\n\n# T\n")]
    [InlineData("<!-- libtmux-logo -->\n<div align=\"center\">\n  <picture>\n    <source srcset=\"assets/logo.svg\" type=\"image/svg+xml\">\n    <img src=\"assets/logo.png\" width=\"128\" height=\"128\" alt=\"libtmux for .NET\">\n  </picture>\n</div>\n<!-- /libtmux-logo -->\n# T\n")]
    public void A_header_that_departs_from_the_shared_one_is_reported(string markdown) =>
        Assert.DoesNotMatch(LogoHeader, markdown);

    [Fact]
    public void A_centered_title_outside_the_markers_is_packed_html()
    {
        string markdown = Header + "<div align=\"center\">\n\n# LibTmux\n\n</div>\n";

        string[] html = [.. PackedHtml(markdown)];

        Assert.Equal(["<div align=\"center\">", "</div>"], html);
    }

    [Fact]
    public void A_centered_title_inside_the_markers_packs_clean()
    {
        string markdown = Header
            + "<!-- libtmux-logo -->\n<div align=\"center\">\n<!-- /libtmux-logo -->\n\n# LibTmux\n\nA client.\n\n"
            + "<!-- libtmux-logo -->\n</div>\n<!-- /libtmux-logo -->\n\nMore.\n";

        Assert.Empty(PackedHtml(markdown));
        Assert.Equal("# LibTmux\n\nA client.\n\nMore.\n", LogoBlock.Replace(markdown, string.Empty));
    }

    [Fact]
    public void A_wrapped_line_that_opens_with_an_autolink_is_packed_html() =>
        Assert.Equal(["<https://github.com/libtmux/libtmux-dotnet>"], PackedHtml("- Issues\n  <https://github.com/libtmux/libtmux-dotnet>\n"));

    [Fact]
    public void Html_inside_a_code_fence_is_not_packed_html() =>
        Assert.Empty(PackedHtml("```html\n<div>\n```\n"));

    [Fact]
    public void A_relative_package_link_is_not_a_heading()
    {
        string markdown = "### [`LibTmux`](src/LibTmux): the client\n";

        Assert.Single(Defects(markdown, ["LibTmux"]), defect => defect.Contains("no heading", StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_paragraph_is_reported() =>
        Assert.Single(Walls("## Quick start\n\n" + string.Join(' ', Enumerable.Repeat("word", 80)) + "\n"));

    [Fact]
    public void A_long_list_item_is_reported_and_its_short_sibling_is_not()
    {
        string markdown = "- short\n- " + string.Join(' ', Enumerable.Repeat("word", 80)) + "\n- also short\n";

        string wall = Assert.Single(Walls(markdown));

        Assert.StartsWith("- word", wall, StringComparison.Ordinal);
    }

    [Fact]
    public void A_paragraph_wrapped_over_several_short_lines_counts_as_one_block() =>
        Assert.Single(Walls(string.Join('\n', Enumerable.Repeat(string.Join(' ', Enumerable.Repeat("word", 12)), 8)) + "\n"));

    [Fact]
    public void A_short_paragraph_with_long_link_targets_is_not_a_wall() =>
        Assert.Empty(Walls($"A [short sentence](https://libtmux.org/{new string('a', 400)}) and a [reference][api-{new string('b', 400)}].\n"));

    [Fact]
    public void A_long_code_fence_a_table_row_and_a_definition_are_not_walls()
    {
        string body = string.Join(' ', Enumerable.Repeat("word", 80));

        Assert.Empty(Walls($"```text\n{body}\n```\n\n| a | {body} |\n\n[label]: https://libtmux.org/{body}\n"));
    }

    [Fact]
    public void The_first_section_is_found_past_a_fence_that_holds_a_heading() =>
        Assert.Equal("Quick start", FirstSection("```markdown\n## Not a section\n```\n\n## Quick start\n\n## Next\n"));

    [Fact]
    public void A_page_that_opens_with_another_section_is_reported() =>
        Assert.Equal("Install", FirstSection("# Title\n\n## Install\n\n## Quick start\n"));

    private static string Section(string package, string path) =>
        $"""
        ### [`{package}`]({Repository}tree/master/{path}): a package

        [NuGet](https://www.nuget.org/packages/{package}) and
        [libtmux.org](https://libtmux.org/en/csharp/latest/reference/).
        """;

    /// <summary>Returns what is missing from a table that indexes the packages under one heading.</summary>
    private static IEnumerable<string> IndexDefects(string markdown, string heading, IEnumerable<string> packages)
    {
        int start = markdown.IndexOf($"\n{heading}\n", StringComparison.Ordinal);
        if (start < 0 && !markdown.StartsWith($"{heading}\n", StringComparison.Ordinal))
        {
            yield return $"the index heading {heading} is missing";
            yield break;
        }

        string rest = markdown[Math.Max(start, 0)..];
        Match next = Regex.Match(rest[(heading.Length + 1)..], @"^## ", RegexOptions.Multiline);
        string section = next.Success ? rest[..(heading.Length + 1 + next.Index)] : rest;
        foreach (string package in packages)
        {
            if (!Regex.IsMatch(section, $@"^\|\s*\**\[{Regex.Escape(package)}\]", RegexOptions.Multiline))
            {
                yield return $"the index under {heading} has no row for {package}";
            }
        }
    }

    /// <summary>Returns the HTML lines nuget.org would show as text once the logo blocks are stripped.</summary>
    private static IEnumerable<string> PackedHtml(string markdown)
    {
        bool fenced = false;
        foreach (string line in LogoBlock.Replace(markdown, string.Empty).Split('\n'))
        {
            if (Regex.IsMatch(line, @"^ {0,3}(```|~~~)"))
            {
                fenced = !fenced;
                continue;
            }

            if (!fenced && Regex.IsMatch(line, @"^ {0,3}</?[A-Za-z]"))
            {
                yield return line.Trim();
            }
        }
    }

    /// <summary>Returns what is wrong with the package list in a document.</summary>
    private static IEnumerable<string> Defects(string markdown, IEnumerable<string> packages)
    {
        Dictionary<string, string> headings = [];
        foreach (Match match in PackageHeading.Matches(markdown))
        {
            headings[match.Groups["name"].Value] = match.Groups["path"].Value;
        }

        foreach (string package in packages)
        {
            if (!headings.TryGetValue(package, out string? path))
            {
                yield return $"no heading links the directory of {package}";
                continue;
            }

            if (path != $"src/{package}")
            {
                yield return $"the heading for {package} links {path}, not src/{package}";
                continue;
            }

            string section = Targets(markdown, SectionOf(markdown, package));
            if (!section.Contains($"https://www.nuget.org/packages/{package}", StringComparison.Ordinal))
            {
                yield return $"the section for {package} has no NuGet link";
            }

            if (!Regex.IsMatch(section, @"https://libtmux\.org/en/(csharp|fsharp)/latest/"))
            {
                yield return $"the section for {package} has no libtmux.org link";
            }
        }
    }

    /// <summary>Returns a section with each reference-style link followed by the address its definition gives.</summary>
    private static string Targets(string markdown, string section)
    {
        Dictionary<string, string> definitions = [];
        foreach (Match definition in Regex.Matches(markdown, @"^\[(?<label>[^\]]+)\]:[ \t]*(?<url>\S+)", RegexOptions.Multiline))
        {
            definitions[definition.Groups["label"].Value] = definition.Groups["url"].Value;
        }

        IEnumerable<string> resolved = Regex.Matches(section, @"\]\[(?<label>[^\]]+)\]")
            .Select(match => definitions.GetValueOrDefault(match.Groups["label"].Value, string.Empty));
        return section + "\n" + string.Join('\n', resolved);
    }

    /// <summary>Returns the text under the heading that links a package, up to the next heading.</summary>
    private static string SectionOf(string markdown, string package)
    {
        Match heading = PackageHeading.Matches(markdown)
            .First(match => match.Groups["name"].Value == package);
        Match next = Heading.Match(markdown, heading.Index + heading.Length);
        return markdown[heading.Index..(next.Success ? next.Index : markdown.Length)];
    }

    /// <summary>Returns the title of the first second-level heading outside a code fence.</summary>
    private static string? FirstSection(string markdown)
    {
        bool fenced = false;
        foreach (string line in markdown.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (!fenced && line.StartsWith("## ", StringComparison.Ordinal))
            {
                return line[3..].Trim();
            }
        }

        return null;
    }

    /// <summary>Returns each paragraph and list item whose rendered text is longer than a reader takes in at once.</summary>
    private static IEnumerable<string> Walls(string markdown)
    {
        foreach (string block in Blocks(markdown))
        {
            string rendered = Render(block);
            if (rendered.Length > LongestBlock)
            {
                yield return rendered.Length > 80 ? rendered[..80] + "..." : rendered;
            }
        }
    }

    /// <summary>Splits prose outside code fences into paragraphs and list items.</summary>
    private static IEnumerable<string> Blocks(string markdown)
    {
        bool fenced = false;
        List<string> current = [];
        foreach (string line in markdown.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            if (line.Trim().Length == 0 || NotProse.IsMatch(line))
            {
                if (current.Count > 0)
                {
                    yield return string.Join(' ', current);
                    current = [];
                }

                continue;
            }

            if (ListItem.IsMatch(line) && current.Count > 0)
            {
                yield return string.Join(' ', current);
                current = [];
            }

            current.Add(line.Trim());
        }

        if (current.Count > 0)
        {
            yield return string.Join(' ', current);
        }
    }

    /// <summary>Returns the text a reader sees: link targets, emphasis marks, and code ticks are gone.</summary>
    private static string Render(string block) =>
        Regex.Replace(LinkTarget.Replace(block, "]"), @"[`*]|(?<=^|\s)\[|\](?=\W|$)", string.Empty);

    /// <summary>Returns the repository-relative paths the absolute links in a document name that do not exist.</summary>
    private static IEnumerable<string> Unresolved(string markdown, string root)
    {
        foreach (Match link in RepositoryLink.Matches(markdown))
        {
            string path = link.Groups["path"].Value.TrimEnd('.', ',', ';', ':');
            string full = Path.Combine(root, path);
            bool exists = link.Groups["kind"].Value == "tree"
                ? Directory.Exists(full)
                : File.Exists(full);
            if (!exists)
            {
                yield return path;
            }
        }
    }

    /// <summary>Returns the name of every package directory, which is each project that packs a README.</summary>
    private static string[] Packages() =>
    [
        .. Directory.GetDirectories(Path.Combine(RepositoryRoot(), "src"))
            .Where(directory => File.Exists(Path.Combine(directory, "README.md")))
            .Select(directory => Path.GetFileName(directory))
            .Order(StringComparer.Ordinal),
    ];

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
