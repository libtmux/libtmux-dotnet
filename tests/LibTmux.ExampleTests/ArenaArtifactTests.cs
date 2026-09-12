using System.Runtime.Versioning;
using LibTmux.Examples;

namespace LibTmux.ExampleTests;

/// <summary>Holds the arena artifact table to what --arena can actually select.</summary>
/// <remarks>
/// The table lives once, in <see cref="ExampleCase.ArenaArtifactsByCase"/>; an
/// example added without an entry, or an entry left behind after a rename,
/// should fail here rather than surface as a runtime "does not name an arena
/// artifact" error.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class ArenaArtifactTests
{
    [Fact]
    public void Every_discovered_example_has_exactly_one_arena_artifact()
    {
        IReadOnlyList<ExampleCase> cases = ExampleCase.Discover();
        string[] missing =
        [
            .. cases
                .Where(example => string.IsNullOrWhiteSpace(example.ArenaArtifact))
                .Select(example => $"{example.Topic}.{example.Id}"),
        ];
        Assert.True(
            missing.Length == 0,
            "These examples have no arena artifact id:\n  " + string.Join("\n  ", missing));

        string[] artifacts = [.. cases.Select(example => example.ArenaArtifact!)];
        Assert.Equal(artifacts.Length, artifacts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_arena_table_names_no_example_that_does_not_exist()
    {
        HashSet<string> discovered =
        [
            .. ExampleCase.Discover().Select(example => $"{example.Topic}.{example.Id}"),
        ];
        string[] orphans =
        [
            .. ExampleCase.ArenaArtifactsByCase.Keys.Where(key => !discovered.Contains(key)),
        ];

        Assert.True(
            orphans.Length == 0,
            "The arena table names examples that do not exist:\n  " + string.Join("\n  ", orphans));
    }

    [Fact]
    public void Every_arena_artifact_id_resolves_back_to_its_own_example()
    {
        foreach ((string key, string artifact) in ExampleCase.ArenaArtifactsByCase)
        {
            ExampleCase? found = ExampleCase.FindByArenaArtifact(artifact);
            Assert.NotNull(found);
            Assert.Equal(key, $"{found!.Topic}.{found.Id}");
        }
    }

    [Fact]
    public void An_unknown_artifact_id_finds_no_example() =>
        Assert.Null(ExampleCase.FindByArenaArtifact("csharp-does-not-exist"));
}
