using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LibTmux.Internal;
using Microsoft.Win32.SafeHandles;

namespace LibTmux.Workspace.Cli;

internal enum LoadMode { Detached, Append, Attach, Switch }

[UnsupportedOSPlatform("windows")]
internal sealed partial class LoadHandoff(CliContext context, Invocation invocation, Output output)
{
    internal LoadMode Mode { get; private set; }
    internal Server? Server { get; private set; }
    internal Session? CurrentSession { get; private set; }
    internal string? CurrentSessionName { get; private set; }
    private ServerGeneration _serverGeneration;
    private Pane? _pane;
    private string? _tty, _window;
    private Client? _client;

    internal async Task ResolveAsync(ServerConnectionOptions options, string sessionName)
    {
        // -d always builds detached, whether or not --append was also given.
        if (invocation.Flag("detached")) { Mode = LoadMode.Detached; return; }
        Mode = invocation.Flag("append") ? LoadMode.Append : LoadMode.Detached;

        if (string.IsNullOrEmpty(context.Environment.GetValueOrDefault("TMUX")))
        {
            if (Mode == LoadMode.Append) throw new CliException("usage", "--append requires a current tmux pane. Use -d.", 2);
            if (!invocation.Flag("yes") && CanPrompt() && await SessionExistsAsync(options, sessionName).ConfigureAwait(false))
            {
                string answer = await ChooseAsync(sessionName + " is already running. Attach? [Y/n] ", ["y", "n"], "y").ConfigureAwait(false);
                if (answer == "n") return;
            }
            _tty = TerminalInput.RequireForeground(context);
            Mode = LoadMode.Attach;
            return;
        }

        await AuthenticateServerAsync(options).ConfigureAwait(false);

        if (Mode == LoadMode.Append)
        {
            await RequireInvokingPaneAsync().ConfigureAwait(false);
            return;
        }

        bool exists = await SessionExistsAsync(options, sessionName).ConfigureAwait(false);
        if (!invocation.Flag("yes") && CanPrompt())
        {
            string answer = exists
                ? await ChooseAsync(sessionName + " is already running. Attach? [Y/n] ", ["y", "n"], "y").ConfigureAwait(false)
                : await ChooseAsync("Already inside tmux: switch (y), load detached (n), or append (a)? [y/n/a] ", ["y", "n", "a"], "y").ConfigureAwait(false);
            if (answer == "n") return;
            Mode = answer == "a" ? LoadMode.Append : LoadMode.Switch;
        }
        else Mode = LoadMode.Switch;

        if (Mode == LoadMode.Append)
        {
            await RequireInvokingPaneAsync().ConfigureAwait(false);
            return;
        }

        // When the invoking pane cannot be identified -- a run-shell key
        // binding sets TMUX but no TMUX_PANE -- switch without picking a
        // client, and skip everything below that needs one.
        if (!await TryResolveInvokingPaneAsync().ConfigureAwait(false)) return;

        IReadOnlyList<Client> clients = await Server!.GetClientsAsync(context.CancellationToken).ConfigureAwait(false);
        RefuseIndependent(clients);
        Client[] eligible = clients.Where(Eligible).OrderBy(client => client.Name, StringComparer.Ordinal).ToArray();
        if (eligible.Length == 0) throw new CliException("client_required", "No ordinary client is viewing the invoking pane. Use -d or --append.");
        if (eligible.Length == 1) _client = eligible[0];
        else
        {
            if (invocation.Flag("yes")) throw new CliException("ambiguous_client", "More than one client is viewing the invoking pane. Choose a client interactively, or use -d or --append.");
            for (int index = 0; index < eligible.Length; index++)
                await output.PromptAsync($"{index + 1}: {eligible[index].Name}\n").ConfigureAwait(false);
            string[] choices = Enumerable.Range(1, eligible.Length).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();
            string answer = await ChooseAsync("Choose client: ", choices).ConfigureAwait(false);
            _client = eligible[int.Parse(answer, CultureInfo.InvariantCulture) - 1];
        }
    }

    // Prompting reads from the real terminal, so it never fires against a
    // pipe: a script sees the same "yes" every port defaults to.
    private static bool CanPrompt() => !Console.IsInputRedirected;

    private async Task<bool> SessionExistsAsync(ServerConnectionOptions options, string sessionName)
    {
        Server probe = Server ??= LibTmux.Server.Open(options);
        TmuxCommandResult result = await probe.ExecuteCommandAsync(["has-session", "-t", "=" + sessionName], context.CancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    // Refuses a load aimed at a different tmux server before anything is
    // built. The current server is already known live -- its socket came
    // from $TMUX -- so a selected endpoint this cannot reach or that reports
    // a different generation is by definition a different server, whether or
    // not it happens to be running yet.
    private async Task AuthenticateServerAsync(ServerConnectionOptions options)
    {
        string? socket = CurrentSocket(context, out int processId);
        if (socket is null) throw new CliException("session_required", "TMUX must identify the current tmux server. Use -d.");
        Server inherited = LibTmux.Server.Open(new ServerConnectionOptions { TmuxBinaryPath = options.TmuxBinaryPath, SocketPath = Path.GetFullPath(socket, context.Directory), ChildEnvironment = context.Environment });
        TmuxCommandResult observed = await inherited.ExecuteCommandAsync(["display-message", "-p", TmuxConnection.GenerationFormat], context.CancellationToken).ConfigureAwait(false);
        if (observed.ExitCode != 0) throw new CliException("session_required", "The current tmux pane is unavailable.");
        ServerGeneration inheritedGeneration = TmuxConnection.ParseGeneration(Encoding.UTF8.GetString(observed.StandardOutput.Span).TrimEnd('\n'));
        if (inheritedGeneration.ProcessId != processId)
            throw new CliException("stale_environment", "The server recorded in TMUX has been replaced.");
        Server target;
        try
        {
            target = await LibTmux.Server.ConnectAsync(options, context.CancellationToken).ConfigureAwait(false);
        }
        catch (LibTmuxException)
        {
            throw new CliException("usage", "Load cannot hand off or append to a different server from the current tmux pane. Use -d.", 2);
        }
        if (target.Generation!.Value != inheritedGeneration)
            throw new CliException("usage", "Load cannot hand off or append to a different server from the current tmux pane. Use -d.", 2);
        Server = target;
        _serverGeneration = inheritedGeneration;
    }

    private async Task RequireInvokingPaneAsync()
    {
        if (!PaneId.TryParse(context.Environment.GetValueOrDefault("TMUX_PANE"), out PaneId paneId))
            throw new CliException("usage", "--append requires a current tmux pane. Use -d.", 2);
        await ResolvePaneSessionAsync(paneId).ConfigureAwait(false);
    }

    private async Task<bool> TryResolveInvokingPaneAsync()
    {
        if (!PaneId.TryParse(context.Environment.GetValueOrDefault("TMUX_PANE"), out PaneId paneId)) return false;
        await ResolvePaneSessionAsync(paneId).ConfigureAwait(false);
        _tty = TerminalInput.RequireForeground(context);
        Pane pane = await Server!.GetPaneAsync(paneId, context.CancellationToken).ConfigureAwait(false);
        _pane = await pane.RefreshAsync(context.CancellationToken).ConfigureAwait(false);
        if (_pane.RawFormatFields.GetValueOrDefault("pane_tty") != _tty)
            throw new CliException("pane_terminal_mismatch", "TMUX_PANE does not name the invoking terminal's pane. Use -d.");
        _window = _pane.RawFormatFields.GetValueOrDefault("window_id")
            ?? throw new CliException("session_required", "The invoking pane has no window.");
        return true;
    }

    private async Task ResolvePaneSessionAsync(PaneId paneId)
    {
        Pane pane = await Server!.GetPaneAsync(paneId, context.CancellationToken).ConfigureAwait(false);
        TmuxCommandResult resolved = await pane.ExecuteCommandAsync(["display-message", "-p", "#{session_id}\t#{session_name}"], cancellationToken: context.CancellationToken).ConfigureAwait(false);
        string[] fields = Encoding.UTF8.GetString(resolved.StandardOutput.Span).TrimEnd('\n').Split('\t', 2);
        if (resolved.ExitCode != 0 || fields.Length != 2 || !SessionId.TryParse(fields[0], out SessionId sessionId))
            throw new CliException("session_required", "The current tmux pane has no session.");
        CurrentSession = await Server.GetSessionAsync(sessionId, context.CancellationToken).ConfigureAwait(false);
        CurrentSessionName = fields[1];
    }

    private async Task<string> ChooseAsync(string prompt, string[] choices, string? defaultAnswer = null)
    {
        using SafeFileHandle input = TerminalInput.Open(context);
        while (true)
        {
            await output.PromptAsync(prompt).ConfigureAwait(false);
            string answer = (await TerminalInput.ReadLineAsync(input, context.CancellationToken).ConfigureAwait(false)).Trim().ToLowerInvariant();
            if (answer.Length == 0 && defaultAnswer is not null) return defaultAnswer;
            if (choices.Contains(answer, StringComparer.Ordinal)) return answer;
            await output.PromptAsync("Choose one of " + string.Join(", ", choices) + ".\n").ConfigureAwait(false);
        }
    }

    private static string? Field(Client client, string name) => client.RawFormatFields.GetValueOrDefault(name);
    private bool Eligible(Client client) => !client.IsControlClient && !string.IsNullOrEmpty(client.Tty)
        && Field(client, "window_id") == _window && Field(client, "pane_id") == _pane!.Id.ToString()
        && int.TryParse(Field(client, "client_pid"), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0
        && long.TryParse(Field(client, "client_created"), NumberStyles.None, CultureInfo.InvariantCulture, out long created) && created > 0;
    private void RefuseIndependent(IEnumerable<Client> clients)
    {
        if (clients.Any(client => !client.IsControlClient && Field(client, "window_id") == _window && (Field(client, "client_flags") ?? "").Split(',').Contains("active-pane", StringComparer.Ordinal)))
            throw new CliException("independent_pane", "A client on this window has independent active-pane focus. Use -d or --append.");
    }

    internal async Task CompleteAsync(Session target)
    {
        if (Mode is LoadMode.Detached or LoadMode.Append) return;
        if (Mode == LoadMode.Switch)
        {
            if (target.Generation != _serverGeneration) throw new StaleServerGenerationException("The workspace server changed before handoff.", _serverGeneration, target.Generation);
            if (_pane is null)
            {
                // No invoking pane was identified -- let tmux pick the
                // client itself, as it does for a nested run-shell load.
                TmuxCommandResult switched = await target.ExecuteCommandAsync(["switch-client"], cancellationToken: context.CancellationToken).ConfigureAwait(false);
                if (switched.ExitCode != 0) throw new CliException("attach_failed", "tmux could not switch the client.", switched.ExitCode);
                return;
            }
            if (TerminalInput.RequireForeground(context) != _tty)
                throw new CliException("terminal_changed", "The invoking terminal changed before attachment.");
            Pane current = await _pane.RefreshAsync(context.CancellationToken).ConfigureAwait(false);
            if (current.RawFormatFields.GetValueOrDefault("pane_tty") != _tty || current.RawFormatFields.GetValueOrDefault("window_id") != _window)
                throw new CliException("pane_changed", "The invoking pane changed before handoff.");
            IReadOnlyList<Client> clients = await Server!.GetClientsAsync(context.CancellationToken).ConfigureAwait(false);
            RefuseIndependent(clients);
            Client? selected = clients.FirstOrDefault(client => client.Name == _client!.Name);
            string[] identity = ["client_pid", "client_created", "client_tty", "session_id", "window_id", "pane_id"];
            if (selected is null || !Eligible(selected) || identity.Any(field => string.IsNullOrEmpty(Field(selected, field)) || Field(selected, field) != Field(_client!, field)))
                throw new CliException("client_changed", "The selected client changed before handoff.");
            TmuxCommandResult result = await target.ExecuteCommandAsync(["switch-client", "-c", selected.Name], cancellationToken: context.CancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new CliException("attach_failed", "tmux could not switch the selected client.", result.ExitCode);
        }
        else
        {
            if (TerminalInput.RequireForeground(context) != _tty)
                throw new CliException("terminal_changed", "The invoking terminal changed before attachment.");
            int status = await new ProcessCommands(context, invocation, output).AttachAsync(target).ConfigureAwait(false);
            if (status != 0) throw new CliException("attach_failed", $"tmux attachment exited with status {status}.", status);
        }
    }

    internal static string? CurrentSocket(CliContext context, out int processId)
    {
        processId = 0;
        if (context.Environment.GetValueOrDefault("TMUX") is not string current) return null;
        int last = current.LastIndexOf(',');
        int previous = last > 0 ? current.LastIndexOf(',', last - 1) : -1;
        if (previous <= 0 || !int.TryParse(current.AsSpan(previous + 1, last - previous - 1), NumberStyles.None, CultureInfo.InvariantCulture, out processId) || processId <= 0
            || !int.TryParse(current.AsSpan(last + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int session) || session < 0) return null;
        return current[..previous];
    }

    private static partial class TerminalInput
    {
        internal static string RequireForeground(CliContext context)
        {
            if (!context.Terminal || Console.IsInputRedirected)
                throw new CliException("usage", "Attachment requires terminal input and output. Use -d to load without attaching.", 2);
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new CliException("terminal_unsupported", "Native terminal handoff currently requires Linux x64. Use -d.");
            if (ForegroundGroup(0) != ProcessGroup()) throw new CliException("usage", "Attachment requires the foreground controlling terminal. Use -d.", 2);
            return Name();
        }

        private static unsafe string Name()
        {
            byte* bytes = stackalloc byte[4096];
            if (TerminalName(0, bytes, 4096) != 0) throw new CliException("terminal_required", "Cannot identify the input terminal. Use -d.");
            return Marshal.PtrToStringUTF8((nint)bytes)!;
        }

        internal static SafeFileHandle Open(CliContext context)
        {
            RequireForeground(context);
            const int noControllingTerminal = 0x100, nonBlocking = 0x800, closeOnExec = 0x80000;
            SafeFileHandle handle = OpenFile("/dev/tty", noControllingTerminal | nonBlocking | closeOnExec);
            if (!handle.IsInvalid) return handle;
            handle.Dispose();
            throw new CliException("terminal_required", "Cannot read the controlling terminal. Use -d.");
        }

        internal static async Task<string> ReadLineAsync(SafeFileHandle input, CancellationToken token)
        {
            byte[] buffer = new byte[1];
            StringBuilder line = new();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                nint count = ReadByte(input, buffer);
                if (count == 1)
                {
                    if (buffer[0] == '\n') return line.ToString();
                    if (line.Length == 128) throw new CliException("invalid_choice", "The terminal answer is too long.", 2);
                    if (buffer[0] != '\r') line.Append((char)buffer[0]);
                }
                else if (count == 0) throw new CliException("input_closed", "Terminal input ended before a load mode was selected.", 2);
                else
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error is not 4 and not 11) throw new CliException("input_failed", "Cannot read terminal input.");
                    await Task.Delay(20, token).ConfigureAwait(false);
                }
            }
        }

        private static unsafe nint ReadByte(SafeFileHandle input, byte[] value)
        {
            fixed (byte* bytes = value) return Read(input, bytes, 1);
        }
        [LibraryImport("libc", EntryPoint = "getpgrp")]
        private static partial int ProcessGroup();
        [LibraryImport("libc", EntryPoint = "tcgetpgrp", SetLastError = true)]
        private static partial int ForegroundGroup(int descriptor);
        [LibraryImport("libc", EntryPoint = "ttyname_r")]
        private static unsafe partial int TerminalName(int descriptor, byte* buffer, nuint size);
        [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial SafeFileHandle OpenFile(string path, int flags);
        [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
        private static unsafe partial nint Read(SafeFileHandle descriptor, byte* buffer, nuint size);
    }
}
