using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;

namespace LibTmux.IntegrationTests.Parity;

[UnsupportedOSPlatform("windows")]
public sealed class Component16ParityTests
{
    public static TheoryData<string> OwnedRows =>
    [
        "libtmux.server:Server.bind_key",
        "libtmux.server:Server.clear_prompt_history",
        "libtmux.server:Server.command_prompt",
        "libtmux.server:Server.confirm_before",
        "libtmux.server:Server.delete_buffer",
        "libtmux.server:Server.display_menu",
        "libtmux.server:Server.display_message",
        "libtmux.server:Server.if_shell",
        "libtmux.server:Server.list_buffers",
        "libtmux.server:Server.list_commands",
        "libtmux.server:Server.list_keys",
        "libtmux.server:Server.load_buffer",
        "libtmux.server:Server.lock_server",
        "libtmux.server:Server.run_shell",
        "libtmux.server:Server.save_buffer",
        "libtmux.server:Server.server_access",
        "libtmux.server:Server.set_buffer",
        "libtmux.server:Server.show_buffer",
        "libtmux.server:Server.show_messages",
        "libtmux.server:Server.show_prompt_history",
        "libtmux.server:Server.source_file",
        "libtmux.server:Server.unbind_key",
        "libtmux.server:Server.wait_for",
    ];

    [Theory(
        Skip = "Requires a Unix process environment.",
        SkipType = typeof(UnixTestEnvironment),
        SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [MemberData(nameof(OwnedRows))]
    public async Task Owned_parity_row_has_server_utility_behavior(string pythonSymbolId)
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null"),
            token);

        bool proved = pythonSymbolId switch
        {
            "libtmux.server:Server.bind_key" => await ProvesBindAsync(server, token),
            "libtmux.server:Server.unbind_key" => await ProvesUnbindAsync(server, token),
            "libtmux.server:Server.list_keys" => await ProvesListKeysAsync(server, token),
            "libtmux.server:Server.command_prompt" => await ProvesPromptAsync(server, token),
            "libtmux.server:Server.clear_prompt_history" =>
                await ProvesPromptHistoryAsync(server, clearing: true, token),
            "libtmux.server:Server.show_prompt_history" =>
                await ProvesPromptHistoryAsync(server, clearing: false, token),
            "libtmux.server:Server.confirm_before" => await ProvesConfirmAsync(server, token),
            "libtmux.server:Server.display_menu" => await ProvesMenuAsync(server, token),
            "libtmux.server:Server.display_message" => await ProvesMessageAsync(server, token),
            "libtmux.server:Server.run_shell" => await ProvesRunShellAsync(server, token),
            "libtmux.server:Server.if_shell" => await ProvesIfShellAsync(server, token),
            "libtmux.server:Server.source_file" => await ProvesSourceFileAsync(server, token),
            "libtmux.server:Server.wait_for" => await ProvesWaitForAsync(server, token),
            "libtmux.server:Server.server_access" => await ProvesServerAccessAsync(server, token),
            "libtmux.server:Server.lock_server" => await ProvesLockAsync(server, token),
            "libtmux.server:Server.show_messages" => await ProvesMessagesAsync(server, token),
            "libtmux.server:Server.list_commands" => await ProvesCommandsAsync(server, token),
            "libtmux.server:Server.set_buffer" => await ProvesSetBufferAsync(server, token),
            "libtmux.server:Server.show_buffer" => await ProvesShowBufferAsync(server, token),
            "libtmux.server:Server.delete_buffer" => await ProvesDeleteBufferAsync(server, token),
            "libtmux.server:Server.list_buffers" => await ProvesListBuffersAsync(server, token),
            "libtmux.server:Server.load_buffer" or "libtmux.server:Server.save_buffer" =>
                await ProvesBufferFilesAsync(server, token),
            _ => false,
        };

        Assert.True(proved, $"Parity behavior was not proved for {pythonSymbolId}.");
    }

    private static async Task<bool> ProvesBindAsync(Server server, CancellationToken token)
    {
        await server.BindKeyAsync(
            new BindKeyRequest("F12", ["display-message", "bound"], keyTable: "root"),
            token);
        return (await server.GetKeysAsync("root", cancellationToken: token))
            .Any(line => line.Contains("F12", StringComparison.Ordinal));
    }

    private static async Task<bool> ProvesUnbindAsync(Server server, CancellationToken token)
    {
        await server.BindKeyAsync(
            new BindKeyRequest("F12", ["display-message", "bound"], keyTable: "root"),
            token);
        await server.UnbindKeyAsync(new UnbindKeyRequest("F12", "root"), token);
        return !(await server.GetKeysAsync("root", cancellationToken: token))
            .Any(line => line.Contains("F12", StringComparison.Ordinal));
    }

    private static async Task<bool> ProvesListKeysAsync(Server server, CancellationToken token)
    {
        IReadOnlyList<string> all = await server.GetKeysAsync(cancellationToken: token);
        Assert.NotEmpty(all);

        // Naming a table narrows the answer to that table's bindings.
        IReadOnlyList<string> root = await server.GetKeysAsync("root", cancellationToken: token);
        return root.Count > 0 && root.Count < all.Count;
    }

    private static async Task<bool> ProvesPromptAsync(Server server, CancellationToken token)
    {
        // A prompt has to be shown to somebody, and the test process is not
        // attached, so tmux refuses rather than prompting nobody.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.ShowCommandPromptAsync(
                new CommandPromptRequest("display-message %1", prompt: "say:"),
                token));
        return true;
    }

    private static async Task<bool> ProvesPromptHistoryAsync(
        Server server,
        bool clearing,
        CancellationToken token)
    {
        string capability = clearing
            ? ServerUtilities.ClearPromptHistoryCapability
            : ServerUtilities.ShowPromptHistoryCapability;
        if (!TmuxCapabilities.IsSupported(server.Version!.Value, capability))
        {
            // The command does not exist yet, so nothing is sent.
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => clearing
                    ? server.ClearPromptHistoryAsync(cancellationToken: token)
                    : server.GetPromptHistoryAsync(cancellationToken: token));
            return true;
        }

        if (clearing)
        {
            await server.ClearPromptHistoryAsync(cancellationToken: token);
            return true;
        }

        // tmux answers with a heading per history even when nothing has been
        // typed, so an empty history still has something to report.
        IReadOnlyList<string> history = await server.GetPromptHistoryAsync(
            cancellationToken: token);
        Assert.Contains(history, line => line.StartsWith("History for", StringComparison.Ordinal));
        return true;
    }

    private static async Task<bool> ProvesConfirmAsync(Server server, CancellationToken token)
    {
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.ConfirmBeforeAsync(
                new ConfirmBeforeRequest(["display-message", "confirmed"]),
                token));
        return true;
    }

    private static async Task<bool> ProvesMenuAsync(Server server, CancellationToken token)
    {
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.ShowMenuAsync(
                new DisplayMenuRequest([new TmuxMenuItem("Item", "i", "display-message chosen")]),
                token));
        return true;
    }

    private static async Task<bool> ProvesMessageAsync(Server server, CancellationToken token)
    {
        IReadOnlyList<string>? rendered = await server.DisplayMessageAsync(
            new DisplayMessageRequest("#{socket_path}", returnText: true),
            token);
        Assert.NotNull(rendered);

        // Without the flag that asks for the text, tmux shows it to a client
        // and there is nothing for the caller to read.
        return await server.DisplayMessageAsync(new DisplayMessageRequest("hello"), token) is null;
    }

    private static async Task<bool> ProvesRunShellAsync(Server server, CancellationToken token)
    {
        // tmux 3.3a and 3.4 run the command and keep its output to themselves,
        // so what proves the run on every lane is an effect rather than a
        // returned line.
        IReadOnlyList<string>? output = await server.RunShellAsync(
            new RunShellRequest("echo parity"),
            token);
        Assert.NotNull(output);
        if (output.Count > 0)
        {
            Assert.Contains("parity", string.Join('\n', output), StringComparison.Ordinal);
        }

        await server.RunShellAsync(
            new RunShellRequest("set-option -g @shell-ran yes", asTmuxCommand: true),
            token);
        await WaitForOptionAsync(server, "@shell-ran", "yes", token);

        // Backgrounding means tmux has not waited, so there is nothing to report.
        return await server.RunShellAsync(new RunShellRequest("true", background: true), token)
            is null;
    }

    private static async Task<bool> ProvesIfShellAsync(Server server, CancellationToken token)
    {
        await server.IfShellAsync(
            new IfShellRequest("true", ["set-option", "-g", "@if-then", "taken"]),
            token);
        await server.IfShellAsync(
            new IfShellRequest(
                "false",
                ["set-option", "-g", "@if-then", "wrong"],
                ["set-option", "-g", "@if-else", "taken"]),
            token);
        await WaitForOptionAsync(server, "@if-then", "taken", token);
        await WaitForOptionAsync(server, "@if-else", "taken", token);
        return true;
    }

    private static async Task<bool> ProvesSourceFileAsync(Server server, CancellationToken token)
    {
        string directory = Directory.CreateTempSubdirectory("libtmux-parity").FullName;
        try
        {
            string path = Path.Combine(directory, "libtmux.conf");
            await File.WriteAllTextAsync(path, "set-option -g @sourced yes\n", token);
            await server.SourceFileAsync(path, cancellationToken: token);
            await WaitForOptionAsync(server, "@sourced", "yes", token);

            // A file tmux cannot read is a failure unless it is asked for
            // quietly, and checking one reports what is wrong without running it.
            await Assert.ThrowsAsync<TmuxCommandException>(
                () => server.SourceFileAsync(
                    Path.Combine(directory, "absent.conf"),
                    cancellationToken: token));
            await server.SourceFileAsync(
                Path.Combine(directory, "absent.conf"),
                quiet: true,
                cancellationToken: token);
            return true;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<bool> ProvesWaitForAsync(Server server, CancellationToken token)
    {
        // Signalling and locking answer at once; waiting would not return until
        // somebody else signalled, so it is not something a test can call.
        await server.WaitForAsync(new WaitForRequest("libtmux", TmuxWaitMode.Signal), token);
        await server.WaitForAsync(new WaitForRequest("libtmux", TmuxWaitMode.Lock), token);
        await server.WaitForAsync(new WaitForRequest("libtmux", TmuxWaitMode.Unlock), token);
        Assert.Throws<ArgumentException>(() => new WaitForRequest(" ", TmuxWaitMode.Signal));
        return true;
    }

    private static async Task<bool> ProvesServerAccessAsync(Server server, CancellationToken token)
    {
        if (!TmuxCapabilities.IsSupported(
            server.Version!.Value,
            ServerUtilities.ServerAccessCapability))
        {
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => server.ConfigureAccessAsync(new ServerAccessRequest(list: true), token));
            return true;
        }

        IReadOnlyList<string>? listed = await server.ConfigureAccessAsync(
            new ServerAccessRequest(list: true),
            token);
        Assert.NotNull(listed);

        // Naming nobody and asking for nothing leaves tmux with no user to act
        // on, and it says so rather than doing nothing.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.ConfigureAccessAsync(new ServerAccessRequest(), token));
        return true;
    }

    private static async Task<bool> ProvesLockAsync(Server server, CancellationToken token)
    {
        // Locking every client works with none attached, which is what makes it
        // a server command rather than a client one.
        await server.LockAsync(token);
        return true;
    }

    private static async Task<bool> ProvesMessagesAsync(Server server, CancellationToken token)
    {
        // tmux 3.2a reads its log through a client rather than for the server,
        // so with none attached it says there is nobody to read for.
        foreach (ShowMessagesMode mode in (ShowMessagesMode[])
            [ShowMessagesMode.Messages, ShowMessagesMode.Terminals, ShowMessagesMode.Jobs])
        {
            try
            {
                Assert.NotNull(await server.GetMessagesAsync(mode: mode, cancellationToken: token));
            }
            catch (TmuxCommandException failure)
                when (failure.Message.Contains("no current client", StringComparison.Ordinal))
            {
            }
        }

        return true;
    }

    private static async Task<bool> ProvesCommandsAsync(Server server, CancellationToken token)
    {
        IReadOnlyList<string> all = await server.GetCommandsAsync(cancellationToken: token);
        Assert.Contains(all, line => line.StartsWith("list-commands", StringComparison.Ordinal));

        // Naming one command answers with that command's syntax alone.
        IReadOnlyList<string> one = await server.GetCommandsAsync("list-buffers", token);
        return one.Count == 1 && one[0].StartsWith("list-buffers", StringComparison.Ordinal);
    }

    private static async Task<bool> ProvesSetBufferAsync(Server server, CancellationToken token)
    {
        await server.SetBufferAsync("head", "libtmux-parity", cancellationToken: token);
        await server.SetBufferAsync(
            " and tail",
            "libtmux-parity",
            append: true,
            cancellationToken: token);
        return await server.GetBufferAsync("libtmux-parity", token) == "head and tail";
    }

    private static async Task<bool> ProvesShowBufferAsync(Server server, CancellationToken token)
    {
        await server.SetBufferAsync("read me", "libtmux-parity", cancellationToken: token);
        Assert.Equal("read me", await server.GetBufferAsync("libtmux-parity", token));

        // A buffer nobody set cannot be read.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.GetBufferAsync("libtmux-absent", token));
        return true;
    }

    private static async Task<bool> ProvesDeleteBufferAsync(Server server, CancellationToken token)
    {
        await server.SetBufferAsync("temporary", "libtmux-parity", cancellationToken: token);
        await server.DeleteBufferAsync("libtmux-parity", token);
        return !(await server.GetBuffersAsync(token)).Any(
            buffer => buffer.Name == "libtmux-parity");
    }

    private static async Task<bool> ProvesListBuffersAsync(Server server, CancellationToken token)
    {
        Assert.Empty(await server.GetBuffersAsync(token));
        await server.SetBufferAsync("listed", "libtmux-parity", cancellationToken: token);

        TmuxBuffer buffer = Assert.Single(await server.GetBuffersAsync(token));
        Assert.Equal("libtmux-parity", buffer.Name);
        Assert.Equal(6, buffer.Size);

        // A format renders each buffer the caller's way instead.
        return (await server.GetBufferLinesAsync(new ListBuffersRequest("#{buffer_name}"), token))
            .SequenceEqual(["libtmux-parity"], StringComparer.Ordinal);
    }

    private static async Task<bool> ProvesBufferFilesAsync(Server server, CancellationToken token)
    {
        string directory = Directory.CreateTempSubdirectory("libtmux-parity").FullName;
        try
        {
            await server.SetBufferAsync("written", "libtmux-parity", cancellationToken: token);
            string path = Path.Combine(directory, "buffer.txt");
            await server.SaveBufferAsync(path, "libtmux-parity", cancellationToken: token);
            Assert.Equal("written", (await File.ReadAllTextAsync(path, token)).TrimEnd('\n'));

            await server.LoadBufferAsync(path, "libtmux-loaded", token);
            return (await server.GetBufferAsync("libtmux-loaded", token)).TrimEnd('\n') == "written";
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitForOptionAsync(
        Server server,
        string name,
        string expected,
        CancellationToken token)
    {
        // A conditional command runs on tmux's own schedule, so the option it
        // sets appears a moment after the call returns.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        string? seen = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            IReadOnlyList<TmuxOption> read = await server.Options.GetAsync(
                new GetOptionRequest(name, OptionScope.Session, global: true, quiet: true),
                token);
            seen = read.Count > 0 ? read[0].Value.Raw : null;
            if (string.Equals(seen, expected, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        }

        Assert.Fail($"The option {name} settled at '{seen}' rather than '{expected}'.");
    }
}
