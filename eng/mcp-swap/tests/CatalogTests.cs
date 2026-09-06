namespace LibTmux.McpSwap.Tests;

public sealed class CatalogTests
{
    private static readonly string[] ExpectedNames =
    [
        "claude",
        "codex",
        "cursor",
        "gemini",
        "grok",
        "agy",
        "opencode",
        "pi",
    ];

    [Fact]
    public void CatalogNamesAllEightClientsInCanonicalOrder()
    {
        using TestPaths paths = new();

        ClientCatalog catalog = ClientCatalog.Create(paths.Home, paths.ConfigHome);

        Assert.Equal(ExpectedNames, catalog.Clients.Select(client => client.Name));
        Assert.Equal(ConfigFormat.Toml, catalog["codex"].Format);
        Assert.Equal(ConfigFormat.Jsonc, catalog["opencode"].Format);
        Assert.Equal(ConfigDialect.Claude, catalog["claude"].Dialect);
        Assert.Equal(ConfigDialect.Opencode, catalog["opencode"].Dialect);
    }

    [Fact]
    public void EveryClientPermutationSelectsCanonicalTransactionOrder()
    {
        using TestPaths paths = new();
        ClientCatalog catalog = ClientCatalog.Create(paths.Home, paths.ConfigHome);
        int count = 0;

        foreach (IReadOnlyList<string> permutation in Permutations(ExpectedNames))
        {
            IReadOnlyList<ClientInfo> selected = catalog.Select(permutation);

            Assert.Equal(ExpectedNames, selected.Select(client => client.Name));
            count++;
        }

        Assert.Equal(40_320, count);
    }

    [Fact]
    public void AntigravityAliasSelectsAgyAndUnknownNamesFail()
    {
        using TestPaths paths = new();
        ClientCatalog catalog = ClientCatalog.Create(paths.Home, paths.ConfigHome);

        ClientInfo selected = Assert.Single(catalog.Select(["antigravity"]));
        Assert.Equal("agy", selected.Name);
        Assert.Throws<ArgumentException>(() => catalog.Select(["other"]));
    }

    private static IEnumerable<IReadOnlyList<string>> Permutations(IReadOnlyList<string> values)
    {
        string[] buffer = values.ToArray();
        return Permute(buffer, 0);

        static IEnumerable<IReadOnlyList<string>> Permute(string[] buffer, int offset)
        {
            if (offset == buffer.Length)
            {
                yield return buffer.ToArray();
                yield break;
            }

            for (int index = offset; index < buffer.Length; index++)
            {
                (buffer[offset], buffer[index]) = (buffer[index], buffer[offset]);
                foreach (IReadOnlyList<string> permutation in Permute(buffer, offset + 1))
                {
                    yield return permutation;
                }

                (buffer[offset], buffer[index]) = (buffer[index], buffer[offset]);
            }
        }
    }
}
