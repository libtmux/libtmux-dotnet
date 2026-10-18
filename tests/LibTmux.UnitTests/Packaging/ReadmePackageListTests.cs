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

            string section = SectionOf(markdown, package);
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
