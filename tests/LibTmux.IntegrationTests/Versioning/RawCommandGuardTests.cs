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

    [UnixFact]
    public async Task An_unnamed_break_pane_is_refused_by_every_route_and_the_daemon_lives_on_exact_3_7()
    {
        Assert.SkipUnless(Version == TmuxVersion.Parse("3.7"), "Only exact tmux 3.7 crashes on an unnamed break-pane.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        Pane first = (await server.GetPanesAsync(token))[0];
        Pane second = await first.SplitAsync(cancellationToken: token);
        string pane = second.Id.ToString();

        foreach (string word in new[] { "break-pane", "breakp", "break-p", "br" })
        {
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => server.ExecuteCommandAsync([word, "-d", "-s", pane], token));
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => server.ExecuteCommandAsync(["list-sessions", ";", word, "-d", "-s", pane], token));
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => server.ExecuteCommandAsync(["list-sessions;", word, "-d", "-s", pane], token));
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => server.Chain().Then(new TmuxCommand(word, ["-d", "-s", pane])).ExecuteAsync(token));
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => second.ExecuteCommandAsync([word, "-d"], cancellationToken: token));
        }

        Assert.True(await server.IsAliveAsync(token));

        await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: token);
        foreach (string word in new[] { "break-pane", "breakp", "break-p", "br" })
        {
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                async () => await control.SendAsync(TmuxCommand.Create(word, "-d", "-s", pane), token));
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                async () => await control.SendAsync(TmuxCommand.Create("list-sessions", ";", word, "-d", "-s", pane), token));
        }

        // The session is still usable, and so is the daemon, for a named break-pane.
        _ = await control.SendAsync(TmuxCommand.Create("break-pane", "-d", "-n", "named", "-s", pane), token);
        Assert.True(await server.IsAliveAsync(token));
        Assert.Equal(2, (await server.GetWindowsAsync(token)).Count);
    }

    [UnixFact]
    public async Task A_clipboard_query_is_refused_by_every_route_while_a_client_is_attached_below_3_6a()
    {
        Assert.SkipWhen(Version >= TmuxVersion.Parse("3.6a"), "tmux 3.6a and later survive a client that leaves.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        await using PtyAttachedClientScope attached = await PtyAttachedClientScope.StartAsync(raw, token);
        Client client = await WaitForClientAsync(server, token);

        foreach (string word in new[] { "refresh-client", "refresh-c", "refresh" })
        {
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => server.ExecuteCommandAsync([word, "-t", client.Name, "-l"], token));
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => server.ExecuteCommandAsync(["list-sessions", ";", word, "-t", client.Name, "-l"], token));
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => server.Chain().Then(new TmuxCommand(word, ["-t", client.Name, "-l"])).ExecuteAsync(token));
        }

        Assert.True(await server.IsAliveAsync(token));

        // The typed call leaves the flag out, so it redraws and nothing else.
        await server.RefreshClientAsync(client.Name, requestClipboard: true, cancellationToken: token);
        Assert.True(await server.IsAliveAsync(token));

        // A client leaving before its terminal answers is what ends 3.4 to 3.6;
        // nothing was sent, so there is no query to answer and the daemon stays up.
        await attached.DisposeAsync();
        await Task.Delay(300, token);
        Assert.True(await server.IsAliveAsync(token));
    }

    [UnixFact]
    public async Task A_clipboard_query_is_sent_from_3_6a()
    {
        Assert.SkipUnless(
            Version >= TmuxVersion.Parse("3.6a") && Version < TmuxVersion.Parse("3.7"),
            "A control client is not attached below 3.7, and 3.7 handles the query elsewhere.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(token);
        Server server = await ConnectAsync(raw, token);
        await using PtyAttachedClientScope attached = await PtyAttachedClientScope.StartAsync(raw, token);
        Client client = await WaitForClientAsync(server, token);

        _ = await server.ExecuteCommandAsync(["refresh-client", "-t", client.Name, "-l"], token);

        Assert.True(await server.IsAliveAsync(token));
    }

    private static async Task<Client> WaitForClientAsync(Server server, CancellationToken token)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestBudget.Settle;
        while (DateTimeOffset.UtcNow < deadline)
        {
            IReadOnlyList<Client> clients = await server.GetClientsAsync(token);
            if (clients.Count > 0)
            {
                return clients[0];
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        }

        throw new InvalidOperationException("tmux never reported an attached client.");
    }

    private static Task<Server> ConnectAsync(RawTmuxTestContext raw, CancellationToken token) =>
        Server.ConnectAsync(
            new ServerConnectionOptions
            {
                TmuxBinaryPath = raw.TmuxBinaryPath,
                SocketPath = raw.SocketPath,
                ConfigurationFile = "/dev/null",
            },
            token);
}
