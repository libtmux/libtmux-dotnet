using System.Text.RegularExpressions;

namespace LibTmux.UnitTests.Packaging;

/// <summary>Holds the package lists in the READMEs to the packages that ship.</summary>
/// <remarks>
/// The root README and the core package README each list every package under a
/// heading that links its directory. nuget.org does not rewrite a relative
/// link, so the links are absolute, and an absolute link to this repository's
/// <c>master</c> branch is only as good as the path it names. These tests
/// resolve those paths against the checkout, and they do not reach the network.
/// </remarks>
public sealed class ReadmePackageListTests
{
    private const string Repository = "https://github.com/libtmux/libtmux-dotnet/";

    private static readonly Regex RepositoryLink = new(
        @"https://github\.com/libtmux/libtmux-dotnet/(?<kind>tree|blob)/master/(?<path>[^\s)#>""\]]+)",
        RegexOptions.Compiled);

    private static readonly Regex PackageHeading = new(
        @"^#{2,6} \[`?(?<name>[^\]`]+)`?\]\(https://github\.com/libtmux/libtmux-dotnet/tree/master/(?<path>[^)\s]+)\)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex Heading = new(@"^#{1,3} ", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex RelativeLink = new(
        @"\]\((?<target>(?!https?://|mailto:|#)[^)\s]+)\)|^ {0,3}\[[^\]]+\]:[ \t]*(?<target>(?!https?://|mailto:|#)\S+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex Fence = new(
        @"^```(?<info>[^\r\n]*)\r?\n(?<body>.*?)^```",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

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

    [Fact]
    public void The_root_and_the_core_readme_give_every_package_the_same_example()
    {
        string root = RepositoryRoot();
        Dictionary<string, string> first = Examples(File.ReadAllText(Path.Combine(root, "README.md")));
        Dictionary<string, string> second = Examples(File.ReadAllText(Path.Combine(root, "src/LibTmux/README.md")));

        // The core README is the client's own page, and each lists a different
        // F# program, so those two are compared by their own sections.
        string[] shared = [.. Packages().Except(["LibTmux", "LibTmux.FSharp"])];
        foreach (string package in shared)
        {
            Assert.True(first.ContainsKey(package), $"README.md lists no {package}.");
            Assert.True(second.ContainsKey(package), $"The core README lists no {package}.");
            Assert.Equal(first[package], second[package]);
        }
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

    [Fact]
    public void A_relative_package_link_is_not_a_heading()
    {
        string markdown = "### [`LibTmux`](src/LibTmux): the client\n";

        Assert.Single(Defects(markdown, ["LibTmux"]), defect => defect.Contains("no heading", StringComparison.Ordinal));
    }

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

    /// <summary>Returns each package's code and command blocks, joined, keyed by package.</summary>
    private static Dictionary<string, string> Examples(string markdown)
    {
        Dictionary<string, string> examples = [];
        foreach (Match heading in PackageHeading.Matches(markdown))
        {
            string package = heading.Groups["name"].Value;
            examples[package] = string.Join(
                "\n---\n",
                Fence.Matches(SectionOf(markdown, package))
                    .Where(fence => !fence.Groups["info"].Value.StartsWith("fsharp", StringComparison.Ordinal))
                    .Select(fence => fence.Groups["body"].Value));
        }

        return examples;
    }

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
