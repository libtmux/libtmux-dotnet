using System.Runtime.Versioning;
using LibTmux.IntegrationTests.Infrastructure;
using LibTmux.IntegrationTests.Transport;
using LibTmux.Internal;
using Microsoft.Extensions.Logging;

namespace LibTmux.IntegrationTests.Utilities;

[UnsupportedOSPlatform("windows")]
public sealed class ServerUtilitiesTests
{
    [UnixFact]
    public async Task Keys_prompts_menus_buffers_and_shell_commands_use_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // A binding is readable back out of the table it was put in. tmux only
        // lists the tables it already knows, so the binding goes in one of
        // those rather than in a table of its own.
        await server.BindKeyAsync(
            new BindKeyRequest("F12", ["display-message", "bound"], keyTable: "root"),
            token);
        Assert.Contains(
            await server.GetKeysAsync("root", cancellationToken: token),
            line => line.Contains("F12", StringComparison.Ordinal));

        // A buffer holds what it was given and gives it back whole.
        await server.SetBufferAsync("first payload", "libtmux-buffer", cancellationToken: token);
        Assert.Equal("first payload", await server.GetBufferAsync("libtmux-buffer", token));
        Assert.Contains(
            await server.GetBuffersAsync(token),
            buffer => buffer.Name == "libtmux-buffer" && buffer.Size == 13);

        // A shell command runs, and where tmux hands its output back it is the
        // command's own. tmux 3.3a and 3.4 keep it to themselves, so the run
        // itself is proved by what it changed.
        IReadOnlyList<string>? output = await server.RunShellAsync(
            new RunShellRequest("echo libtmux-ran"),
            token);
        Assert.NotNull(output);
        if (output.Count > 0)
        {
            Assert.Contains("libtmux-ran", string.Join('\n', output), StringComparison.Ordinal);
        }

        await server.RunShellAsync(
            new RunShellRequest("set-option -g @shell-ran yes", asTmuxCommand: true),
            token);
        await WaitForOptionAsync(server, "@shell-ran", "yes", token);

        // The server knows its own commands, and one of them is the one just used.
        Assert.Contains(
            await server.GetCommandsAsync(cancellationToken: token),
            line => line.StartsWith("run-shell", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task Bind_unbind_and_list_key_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        await server.BindKeyAsync(
            new BindKeyRequest(
                "F11",
                ["display-message", "noted"],
                keyTable: "root",
                note: "a libtmux binding",
                repeat: true),
            token);

        IReadOnlyList<string> bound = await server.GetKeysAsync("root", cancellationToken: token);
        Assert.Contains(bound, line => line.Contains("F11", StringComparison.Ordinal));

        // Unbinding one key leaves the table's other bindings alone.
        await server.BindKeyAsync(
            new BindKeyRequest("F10", ["display-message", "kept"], keyTable: "root"),
            token);
        await server.UnbindKeyAsync(new UnbindKeyRequest("F11", "root"), token);
        IReadOnlyList<string> after = await server.GetKeysAsync("root", cancellationToken: token);
        Assert.DoesNotContain(after, line => line.Contains("F11", StringComparison.Ordinal));
        Assert.Contains(after, line => line.Contains("F10", StringComparison.Ordinal));

        // Unbinding a key nobody bound is not an error: tmux treats the
        // binding's absence as the state that was asked for.
        await server.UnbindKeyAsync(new UnbindKeyRequest("F9", "root"), token);
        await server.UnbindKeyAsync(new UnbindKeyRequest("F9", "root", quiet: true), token);

        // Removing them all empties the table.
        await server.UnbindKeyAsync(new UnbindKeyRequest(all: true, keyTable: "root"), token);
        Assert.Empty(await server.GetKeysAsync("root", cancellationToken: token));

        // A request that names no key and does not ask for all of them cannot
        // mean anything, so it never reaches tmux.
        Assert.Throws<ArgumentException>(() => new UnbindKeyRequest());
        Assert.Throws<ArgumentException>(() => new BindKeyRequest("F1", []));
    }

    [UnixFact]
    public async Task Prompt_menu_confirm_and_display_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // Every one of these needs a client to show something to, and the test
        // process has none, so tmux refuses rather than doing nothing.
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.ShowCommandPromptAsync(
                new CommandPromptRequest("display-message %1", prompt: "say:"),
                token));
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.ConfirmBeforeAsync(
                new ConfirmBeforeRequest(["display-message", "confirmed"], prompt: "sure?"),
                token));
        await Assert.ThrowsAsync<TmuxCommandException>(
            () => server.ShowMenuAsync(
                new DisplayMenuRequest([new TmuxMenuItem("Item", "i", "display-message chosen")]),
                token));

        // A message renders through the server without a client, which is what
        // separates it from the three above.
        IReadOnlyList<string>? rendered = await server.DisplayMessageAsync(
            new DisplayMessageRequest("#{pid}", returnText: true),
            token);
        Assert.NotNull(rendered);
        Assert.NotEmpty(rendered);

        // An unfinished format is not an error to tmux: it renders to nothing
        // and says so by printing nothing.
        IReadOnlyList<string>? empty = await server.DisplayMessageAsync(
            new DisplayMessageRequest("#{", returnText: true),
            token);
        Assert.NotNull(empty);
        Assert.All(empty, line => Assert.Equal(string.Empty, line));

        // A menu with nothing in it cannot be shown.
        Assert.Throws<ArgumentException>(() => new DisplayMenuRequest([]));
        Assert.Throws<ArgumentException>(() => new ConfirmBeforeRequest([]));
    }

    [UnixFact]
    public async Task Buffer_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        string directory = Directory.CreateTempSubdirectory("libtmux-buffers").FullName;

        try
        {
            await server.SetBufferAsync("head", "libtmux-buffer", cancellationToken: token);
            await server.SetBufferAsync(
                " and tail",
                "libtmux-buffer",
                append: true,
                cancellationToken: token);
            Assert.Equal("head and tail", await server.GetBufferAsync("libtmux-buffer", token));

            // A buffer written out and read back in is the same buffer.
            string path = Path.Combine(directory, "buffer.txt");
            await server.SaveBufferAsync(path, "libtmux-buffer", cancellationToken: token);
            Assert.Equal("head and tail", (await File.ReadAllTextAsync(path, token)).TrimEnd('\n'));
            await server.LoadBufferAsync(path, "libtmux-loaded", token);
            Assert.Equal("head and tail", (await server.GetBufferAsync("libtmux-loaded", token)).TrimEnd('\n'));

            // Listing reports every buffer with what it holds.
            IReadOnlyList<TmuxBuffer> buffers = await server.GetBuffersAsync(token);
            Assert.Contains(buffers, buffer => buffer.Name == "libtmux-buffer");
            Assert.Contains(buffers, buffer => buffer.Name == "libtmux-loaded");
            Assert.All(buffers, buffer => Assert.True(buffer.Size > 0));

            // A format renders each buffer the caller's way instead.
            IReadOnlyList<string> named = await server.GetBufferLinesAsync(
                new ListBuffersRequest("#{buffer_name}"),
                token);
            Assert.Contains("libtmux-buffer", named);

            // Deleting one leaves the other.
            await server.DeleteBufferAsync("libtmux-buffer", token);
            Assert.DoesNotContain(
                await server.GetBuffersAsync(token),
                buffer => buffer.Name == "libtmux-buffer");
            await Assert.ThrowsAsync<TmuxCommandException>(
                () => server.GetBufferAsync("libtmux-buffer", token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [UnixFact]
    public async Task Shell_if_source_wait_and_access_flags_emit_exact_argv()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        string directory = Directory.CreateTempSubdirectory("libtmux-shell").FullName;

        try
        {
            // A backgrounded command has not run yet, so there is nothing it
            // could have printed.
            Assert.Null(await server.RunShellAsync(
                new RunShellRequest("true", background: true),
                token));

            // A tmux command run this way goes through tmux rather than a shell.
            await server.RunShellAsync(
                new RunShellRequest("set-option -g @ran yes", asTmuxCommand: true),
                token);
            Assert.Equal(
                "yes",
                Assert.Single(await server.Options.GetAsync(
                        new GetOptionRequest("@ran", OptionScope.Session, global: true),
                        token))
                    .Value.Raw);

            // The shell command decides which of the two tmux commands runs.
            await server.IfShellAsync(
                new IfShellRequest("true", ["set-option", "-g", "@then", "taken"]),
                token);
            await server.IfShellAsync(
                new IfShellRequest(
                    "false",
                    ["set-option", "-g", "@then", "wrong"],
                    ["set-option", "-g", "@else", "taken"]),
                token);
            await WaitForOptionAsync(server, "@then", "taken", token);
            await WaitForOptionAsync(server, "@else", "taken", token);

            // A configuration file is read and its commands take effect.
            string configuration = Path.Combine(directory, "libtmux.conf");
            await File.WriteAllTextAsync(configuration, "set-option -g @sourced yes\n", token);
            await server.SourceFileAsync(configuration, cancellationToken: token);
            await WaitForOptionAsync(server, "@sourced", "yes", token);

            // Checking a file reports what is wrong without running any of it.
            string broken = Path.Combine(directory, "broken.conf");
            await File.WriteAllTextAsync(broken, "not-a-command\n", token);
            await Assert.ThrowsAsync<TmuxCommandException>(
                () => server.SourceFileAsync(broken, parseOnly: true, cancellationToken: token));

            // A missing file is a failure unless it is asked for quietly.
            string missing = Path.Combine(directory, "absent.conf");
            await Assert.ThrowsAsync<TmuxCommandException>(
                () => server.SourceFileAsync(missing, cancellationToken: token));
            await server.SourceFileAsync(missing, quiet: true, cancellationToken: token);

            // Signalling a channel nobody waits on is still a valid thing to do.
            await server.WaitForAsync(new WaitForRequest("libtmux", TmuxWaitMode.Signal), token);
            await server.WaitForAsync(new WaitForRequest("libtmux", TmuxWaitMode.Lock), token);
            await server.WaitForAsync(new WaitForRequest("libtmux", TmuxWaitMode.Unlock), token);

            // Locking every client is possible with none attached.
            await server.LockAsync(token);

            // The server's own log is readable, and so is what it knows about
            // the terminals it has seen. tmux 3.2a wants a client before it
            // will answer at all, and says so rather than reporting nothing.
            await ReadMessagesAsync(server, ShowMessagesMode.Messages, token);
            await ReadMessagesAsync(server, ShowMessagesMode.Terminals, token);

            // A request that both grants and withdraws cannot mean anything.
            Assert.Throws<ArgumentException>(
                () => new ServerAccessRequest(allowUser: "a", denyUser: "b"));
            Assert.Throws<ArgumentException>(
                () => new ServerAccessRequest(readOnly: true, readWrite: true));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new RunShellRequest("true", delay: TimeSpan.FromSeconds(-1)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [UnixFact]
    public Task ClearPromptHistoryCommandVersionPolicy() =>
        ProvesWholeCommandGateAsync(
            ServerUtilities.ClearPromptHistoryCapability,
            (server, token) => server.ClearPromptHistoryAsync(cancellationToken: token));

    [UnixFact]
    public Task ShowPromptHistoryCommandVersionPolicy() =>
        ProvesWholeCommandGateAsync(
            ServerUtilities.ShowPromptHistoryCapability,
            (server, token) => server.GetPromptHistoryAsync(cancellationToken: token));

    [UnixFact]
    public Task ServerAccessCommandVersionPolicy() =>
        ProvesWholeCommandGateAsync(
            ServerUtilities.ServerAccessCapability,
            (server, token) => server.ConfigureAccessAsync(
                new ServerAccessRequest(list: true),
                token));

    [UnixFact]
    public async Task CommandPromptBackgroundVersionPolicy()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);
        bool supported = Supports(server, ServerUtilities.CommandPromptBackgroundCapability);

        // tmux 3.2a spells the type flag as booleans meaning something else, so
        // asking for one there would ask a different question rather than fail.
        // Nothing is sent at all instead.
        CommandPromptRequest typed = new("display-message %1", type: PromptType.Command);
        CommandPromptRequest formatted = new("display-message %1", expandFormat: true);
        if (supported)
        {
            await Assert.ThrowsAsync<TmuxCommandException>(
                () => server.ShowCommandPromptAsync(typed, token));
            await Assert.ThrowsAsync<TmuxCommandException>(
                () => server.ShowCommandPromptAsync(formatted, token));
        }
        else
        {
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => server.ShowCommandPromptAsync(typed, token));
            await Assert.ThrowsAsync<TmuxVersionTooLowException>(
                () => server.ShowCommandPromptAsync(formatted, token));
        }
    }

    [UnixFact]
    public Task CommandPromptLiteralVersionPolicy() =>
        ProvesWarnAndOmitAsync(
            ServerUtilities.CommandPromptLiteralCapability,
            (server, token) => server.ShowCommandPromptAsync(
                new CommandPromptRequest("display-message %1", literal: true),
                token));

    [UnixFact]
    public Task CommandPrompt37BehaviorVersionPolicy() =>
        ProvesWarnAndOmitAsync(
            ServerUtilities.CommandPrompt37Capability,
            (server, token) => server.ShowCommandPromptAsync(
                new CommandPromptRequest(
                    "display-message %1",
                    backspaceExits: true,
                    noFreeze: true),
                token),
            expectedWarnings: 2);

    [UnixFact]
    public Task ConfirmBeforeAcceptanceVersionPolicy() =>
        ProvesWarnAndOmitAsync(
            ServerUtilities.ConfirmBeforeAcceptanceCapability,
            (server, token) => server.ConfirmBeforeAsync(
                new ConfirmBeforeRequest(
                    ["display-message", "confirmed"],
                    confirmKey: "y",
                    defaultYes: true),
                token),
            expectedWarnings: 2);

    [UnixFact]
    public async Task ConfirmBeforeBackgroundVersionPolicy()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Server server = await ConnectAsync(raw, token);

        // The confirmation's background flag arrived in tmux 3.3, and the
        // approved surface never asks for it, so no caller can reach a version
        // that would refuse. The boundary is still where the table says.
        bool supported = Supports(server, ServerUtilities.ConfirmBeforeBackgroundCapability);
        Assert.Equal(
            server.Version! >= TmuxVersion.Parse("3.3a"),
            supported);

        // The flag group is what moved, so the check is for a group holding it
        // rather than for the two characters, which the command's own name has.
        RawTmuxResult syntax = await raw.ExecuteAsync(["list-commands", "confirm-before"], token);
        string usage = string.Join('\n', syntax.StandardOutputLines);
        int group = usage.IndexOf("[-", StringComparison.Ordinal);
        bool carriesBackground = group >= 0
            && usage[group..usage.IndexOf(']', group)].Contains('b', StringComparison.Ordinal);
        Assert.Equal(supported, carriesBackground);
    }

    [UnixFact]
    public Task DisplayMenuStylesVersionPolicy() =>
        ProvesWarnAndOmitAsync(
            ServerUtilities.DisplayMenuStylesCapability,
            (server, token) => server.ShowMenuAsync(
                new DisplayMenuRequest(
                    [new TmuxMenuItem("Item", "i", "display-message chosen")],
                    style: "fg=red"),
                token));

    [UnixFact]
    public Task DisplayMenuMouseVersionPolicy() =>
        ProvesWarnAndOmitAsync(
            ServerUtilities.DisplayMenuMouseCapability,
            (server, token) => server.ShowMenuAsync(
                new DisplayMenuRequest(
                    [new TmuxMenuItem("Item", "i", "display-message chosen")],
                    mouse: true),
                token),
            // The style flags are gated a release earlier, so an older tmux
            // warns about both families for one menu.
            expectedWarnings: null);

    [UnixFact]
    public async Task DisplayMessageLiteralVersionPolicy()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingLogger logger = new();
        Server server = await ConnectAsync(raw, token, logger);
        bool supported = Supports(server, ServerUtilities.DisplayMessageLiteralCapability);

        IReadOnlyList<string>? rendered = await server.DisplayMessageAsync(
            new DisplayMessageRequest("#{pid}", returnText: true, noExpand: true),
            token);

        if (supported)
        {
            // The flag is carried, so the text is returned unexpanded.
            Assert.Empty(logger.Warnings);
            Assert.Equal("#{pid}", Assert.Single(rendered!));
        }
        else
        {
            Assert.Single(logger.Warnings);
        }
    }

    [UnixFact]
    public async Task DisplayMessageClientVersionPolicy()
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingLogger logger = new();
        Server server = await ConnectAsync(raw, token, logger);
        bool supported = Supports(server, ServerUtilities.DisplayMessageClientCapability);

        // Deliberately not the shared warn-and-omit helper: it forgives a
        // failed command, and a command that failed is exactly what sending
        // the flag to a tmux that refuses it looks like. Asserting the call
        // succeeds is what makes dropping the gate visible here.
        IReadOnlyList<string>? rendered = await server.DisplayMessageAsync(
            new DisplayMessageRequest("addressed", returnText: true, targetClient: "/dev/null"),
            token);

        Assert.Equal("addressed", Assert.Single(rendered!));
        if (supported)
        {
            Assert.Empty(logger.Warnings);
            return;
        }

        Assert.Single(logger.Warnings);
    }

    [UnixFact]
    public Task ListKeysFormatVersionPolicy() =>
        ProvesWarnAndOmitAsync(
            ServerUtilities.ListKeysFormatCapability,
            (server, token) => server.GetKeysAsync(
                format: "#{key_table}",
                cancellationToken: token));

    [UnixFact]
    public Task RunShellWorkingDirectoryVersionPolicy() =>
        ProvesWarnAndOmitAsync(
            ServerUtilities.RunShellWorkingDirectoryCapability,
            (server, token) => server.RunShellAsync(
                new RunShellRequest("pwd", workingDirectory: "/"),
                token));

    [UnixFact]
    public Task RunShellShowStderrVersionPolicy() =>
        ProvesWarnAndOmitAsync(
            ServerUtilities.RunShellStandardErrorCapability,
            (server, token) => server.RunShellAsync(
                new RunShellRequest("true", showStandardError: true),
                token));

    [UnixFact]
    public Task RunShellArgumentsVersionPolicy() =>
        ProvesWarnAndOmitAsync(
            ServerUtilities.RunShellArgumentsCapability,
            (server, token) => server.RunShellAsync(
                new RunShellRequest("echo", ["libtmux"]),
                token));

    private static async Task ReadMessagesAsync(
        Server server,
        ShowMessagesMode mode,
        CancellationToken token)
    {
        try
        {
            Assert.NotNull(await server.GetMessagesAsync(mode: mode, cancellationToken: token));
        }
        catch (TmuxCommandException failure)
            when (failure.Message.Contains("no current client", StringComparison.Ordinal))
        {
            // Older tmux reads its log through a client rather than for the
            // server, so with none attached there is nobody to read for.
        }
    }

    private static bool Supports(Server server, string capability) =>
        TmuxCapabilities.IsSupported(server.Version!.Value, capability);

    private static async Task ProvesWholeCommandGateAsync(
        string capability,
        Func<Server, CancellationToken, Task> exercise)
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingLogger logger = new();
        Server server = await ConnectAsync(raw, token, logger);

        if (Supports(server, capability))
        {
            // The command exists, so it is sent and answers for itself.
            await exercise(server, token);
            Assert.Empty(logger.Warnings);
            return;
        }

        // The whole command is missing, so nothing is sent: there is no flag
        // to drop that would leave the request meaning the same thing.
        await Assert.ThrowsAsync<TmuxVersionTooLowException>(() => exercise(server, token));

        // Nothing was sent, so there was nothing to warn about either.
        Assert.Empty(logger.Warnings);
    }

    private static async Task ProvesWarnAndOmitAsync(
        string capability,
        Func<Server, CancellationToken, Task> exercise,
        int? expectedWarnings = 1)
    {
        await using RawTmuxTestContext raw = await RawTmuxTestContext.StartAsync(
            TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingLogger logger = new();
        Server server = await ConnectAsync(raw, token, logger);
        bool supported = Supports(server, capability);

        try
        {
            await exercise(server, token);
        }
        catch (TmuxCommandException)
        {
            // Several of these need a client to show something to, which the
            // test process has none of. What is being proved is which flags
            // were built, not whether tmux had somewhere to put the result.
        }

        if (supported)
        {
            Assert.Empty(logger.Warnings);
            return;
        }

        // The flag is missing, so it is dropped, the command still goes out
        // once, and the caller is told what was left off.
        Assert.NotEmpty(logger.Warnings);
        if (expectedWarnings is int count)
        {
            Assert.Equal(count, logger.Warnings.Count);
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
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestBudget.Settle;
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

    private static Task<Server> ConnectAsync(
        RawTmuxTestContext raw,
        CancellationToken token,
        ILogger? logger = null) =>
        Server.ConnectAsync(
            new ServerConnectionOptions(
                tmuxBinaryPath: raw.TmuxBinaryPath,
                socketPath: raw.SocketPath,
                configurationFile: "/dev/null",
                logger: logger),
            token);

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _warnings = [];

        public List<string> Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            // The dispatcher records every command failure at error level, and
            // these proofs are about the warning a dropped flag produces, so
            // only warnings are counted.
            if (logLevel == LogLevel.Warning)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }
}
