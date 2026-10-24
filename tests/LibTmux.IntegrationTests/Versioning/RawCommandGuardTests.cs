using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Versioning;

/// <summary>Proves every public path that sends a command line refuses what the running tmux would not survive.</summary>
/// <remarks>
/// Each test sends the command by every route the library offers and then
/// asks the daemon whether it is still alive, since a command that reaches
/// an affected release does not fail, it takes the server down.
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class RawCommandGuardTests
{
    private static TmuxVersion Version => TmuxFeatureSupport.Version;

    [UnixFact]
    public async Task Our_command_name_table_is_the_one_the_running_tmux_lists()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);

        RawTmuxResult listed = await raw.ExecuteAsync(
            ["list-commands", "-F", "#{command_list_name} #{command_list_alias}"], token);

        string[] expected = [.. listed.StandardOutputLines.Select(static line => line.TrimEnd()).Order(StringComparer.Ordinal)];
        string[] actual = [.. TmuxCommandNames.Listed(Version)
            .Select(static entry => $"{entry.Name} {entry.Alias}".TrimEnd())
            .Order(StringComparer.Ordinal)];
        Assert.Equal(expected, actual);
    }

    [UnixFact]
    public async Task Every_prefix_of_every_command_resolves_as_the_running_tmux_resolves_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);

        SortedSet<string> words = new(StringComparer.Ordinal);
        foreach ((string name, string? alias) in TmuxCommandNames.Listed(Version))
        {
            // Prefixes of an alias are words too: tmux must refuse them.
            foreach (string word in new[] { name, alias ?? name })
            {
                for (int length = 1; length <= word.Length; length++)
                {
                    words.Add(word[..length]);
                }
            }
        }

        List<string> differences = [];
        foreach (string word in words)
        {
            // An unknown flag fails the parse, so no command runs, and the
            // error names the command the word was read as.
            RawTmuxResult result = await raw.ExecuteAsync([word, "-@"], token);
            Match named = Regex.Match(result.StandardErrorText, "^command ([a-z-]+): ", RegexOptions.CultureInvariant);
            string? tmux = named.Success ? named.Groups[1].Value : null;
            if (tmux is null
                && !result.StandardErrorText.Contains("ambiguous command", StringComparison.Ordinal)
                && !result.StandardErrorText.Contains("unknown command", StringComparison.Ordinal))
            {
                differences.Add($"{word}: tmux said '{result.StandardErrorText.Trim()}'");
                continue;
            }

            string? ours = TmuxCommandNames.Resolve(Version, word);
            if (ours != tmux)
            {
                differences.Add($"{word}: tmux {tmux ?? "refuses"}, resolver {ours ?? "refuses"}");
            }
        }

        Assert.Empty(differences);
    }
}
