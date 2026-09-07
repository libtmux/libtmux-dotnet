using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibTmux.Examples;

namespace LibTmux.ExampleTests;

/// <summary>Holds the published blocks to the code that actually runs.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class SnippetContractTests
{
    private static readonly Regex Region = new(
        @"^\s*#region\s+(?<name>\S+)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void Every_published_region_is_an_explicit_example()
    {
        HashSet<string> examples =
        [
            .. typeof(ExampleCase).Assembly
                .GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(method => method.GetCustomAttribute<ExampleAttribute>() is not null)
                .Select(method => method.Name),
        ];

        List<string> orphans = [];
        foreach (string file in SnippetFiles())
        {
            foreach (Match match in Region.Matches(File.ReadAllText(file)))
            {
                string name = match.Groups["name"].Value;
                if (!examples.Contains(name))
                {
                    orphans.Add($"{Path.GetFileName(file)}: #region {name}");
                }
            }
        }

        Assert.True(
            orphans.Count == 0,
            "These regions are published but have no [Example] method:\n  "
            + string.Join("\n  ", orphans));
    }

    [Fact]
    public void Every_ordinary_published_example_runs_in_the_default_suite()
    {
        HashSet<string> runnable =
        [
            .. ExampleCase.Discover().Select(example => $"{example.Topic}.{example.Id}"),
        ];
        string[] skipped =
        [
            .. typeof(ExampleCase).Assembly
                .GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(method => method.GetCustomAttribute<ExampleAttribute>() is not null)
                .Where(method => method.DeclaringType!.Name != "Psmux")
                .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
                .Where(example => !runnable.Contains(example))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            skipped.Length == 0,
            "These ordinary published examples do not run against live tmux:\n  "
            + string.Join("\n  ", skipped));
    }

    [Fact]
    public void Every_example_lives_where_the_snippet_reader_looks()
    {
        // sync_snippets.py globs this one directory.
        IReadOnlyList<string> files = SnippetFiles();
        Assert.NotEmpty(files);

        HashSet<string> topics =
        [
            .. files.Select(file => Path.GetFileNameWithoutExtension(file)),
        ];
        foreach (ExampleCase example in ExampleCase.Discover())
        {
            Assert.Contains(example.Topic, topics);
        }
    }

    private static IReadOnlyList<string> SnippetFiles() =>
        [.. Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot(), "examples", "LibTmux.Examples", "Snippets"),
            "*.cs")];

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
