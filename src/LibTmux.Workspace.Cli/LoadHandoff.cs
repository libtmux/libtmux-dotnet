using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
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
    private Pane? _pane;
    private string? _tty, _window;
    private Client? _client;

    internal async Task ResolveAsync(ServerConnectionOptions options)
    {
        Mode = invocation.Flag("append") ? LoadMode.Append : LoadMode.Detached;
        if (invocation.Flag("detached") && Mode != LoadMode.Append) return;
        if (Mode != LoadMode.Append)
        {
            _tty = TerminalInput.RequireForeground(context);
            if (string.IsNullOrEmpty(context.Environment.GetValueOrDefault("TMUX")))
            {
                Mode = LoadMode.Attach;
                return;
            }
            if (CurrentSocket(context, out _) is null || !PaneId.TryParse(context.Environment.GetValueOrDefault("TMUX_PANE"), out _))
                throw new CliException("session-required", "TMUX and TMUX_PANE must identify the current tmux pane. Use -d.");
            if (!invocation.Flag("yes"))
            {
                string answer = await ChooseAsync("Load workspace: [y] switch, [n] detached, [a] append: ", ["y", "n", "a"]).ConfigureAwait(false);
                if (answer == "n") return;
                Mode = answer == "a" ? LoadMode.Append : LoadMode.Switch;
            }
            else Mode = LoadMode.Switch;
        }
        await ResolveCurrentAsync(options).ConfigureAwait(false);
        if (Mode == LoadMode.Append) return;
        IReadOnlyList<Client> clients = await Server!.GetClientsStrictAsync(context.CancellationToken).ConfigureAwait(false);
        RefuseIndependent(clients);
        Client[] eligible = clients.Where(Eligible).OrderBy(client => client.Name, StringComparer.Ordinal).ToArray();
        if (eligible.Length == 0) throw new CliException("client-required", "No ordinary client is viewing the invoking pane. Use -d or --append.");
        if (eligible.Length == 1) _client = eligible[0];
        else
        {
            if (invocation.Flag("yes")) throw new CliException("ambiguous-client", "More than one client is viewing the invoking pane. Choose a client interactively, or use -d or --append.");
            for (int index = 0; index < eligible.Length; index++)
                await output.PromptAsync($"{index + 1}: {eligible[index].Name}\n").ConfigureAwait(false);
            string[] choices = Enumerable.Range(1, eligible.Length).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();
            string answer = await ChooseAsync("Choose client: ", choices).ConfigureAwait(false);
            _client = eligible[int.Parse(answer, CultureInfo.InvariantCulture) - 1];
        }
    }

    private async Task<string> ChooseAsync(string prompt, string[] choices)
    {
        using SafeFileHandle input = TerminalInput.Open(context);
        while (true)
        {
            await output.PromptAsync(prompt).ConfigureAwait(false);
            string answer = (await TerminalInput.ReadLineAsync(input, context.CancellationToken).ConfigureAwait(false)).Trim().ToLowerInvariant();
            if (choices.Contains(answer, StringComparer.Ordinal)) return answer;
            await output.PromptAsync("Choose one of " + string.Join(", ", choices) + ".\n").ConfigureAwait(false);
        }
    }

    private async Task ResolveCurrentAsync(ServerConnectionOptions options)
    {
        string? socket = CurrentSocket(context, out int processId);
        if (socket is null || !PaneId.TryParse(context.Environment.GetValueOrDefault("TMUX_PANE"), out PaneId paneId))
            throw new CliException("session-required", "Load requires TMUX and TMUX_PANE from the current tmux pane. Use -d outside tmux.");
        Server inherited = LibTmux.Server.Open(new ServerConnectionOptions(tmuxBinaryPath: options.TmuxBinaryPath, socketPath: Path.GetFullPath(socket, context.Directory), childEnvironment: context.Environment));
        TmuxCommandResult observed = await inherited.ExecuteCommandAsync(["display-message", "-p", "-t", paneId.ToString(), "#{pid}:#{start_time}"], context.CancellationToken).ConfigureAwait(false);
        string generation = Encoding.UTF8.GetString(observed.StandardOutput.Span).TrimEnd('\n');
        if (observed.ExitCode != 0) throw new CliException("session-required", "The current tmux pane is unavailable.");
        if (!generation.StartsWith(processId.ToString(CultureInfo.InvariantCulture) + ":", StringComparison.Ordinal))
            throw new CliException("stale-environment", "The server recorded in TMUX has been replaced.");
        Server = await LibTmux.Server.ConnectAsync(options, context.CancellationToken).ConfigureAwait(false);
        ServerGeneration selected = Server.Generation!.Value;
        if (generation != string.Create(CultureInfo.InvariantCulture, $"{selected.ProcessId}:{selected.StartTime}"))
            throw new CliException("endpoint-mismatch", "Load cannot hand off or append to a different server from the current tmux pane. Use -d.");
        Pane pane = await Server.GetPaneAsync(paneId, context.CancellationToken).ConfigureAwait(false);
        TmuxCommandResult resolved = await pane.ExecuteCommandAsync(["display-message", "-p", "#{session_id}\t#{session_name}"], cancellationToken: context.CancellationToken).ConfigureAwait(false);
        string[] fields = Encoding.UTF8.GetString(resolved.StandardOutput.Span).TrimEnd('\n').Split('\t', 2);
        if (resolved.ExitCode != 0 || fields.Length != 2 || !SessionId.TryParse(fields[0], out SessionId sessionId))
            throw new CliException("session-required", "The current tmux pane has no session.");
        CurrentSession = await Server.GetSessionAsync(sessionId, context.CancellationToken).ConfigureAwait(false);
        CurrentSessionName = fields[1];
        if (Mode == LoadMode.Append) return;
        _pane = await pane.RefreshAsync(context.CancellationToken).ConfigureAwait(false);
        if (_pane.RawFormatFields.GetValueOrDefault("pane_tty") != _tty)
            throw new CliException("pane-terminal-mismatch", "TMUX_PANE does not name the invoking terminal's pane. Use -d.");
        _window = _pane.RawFormatFields.GetValueOrDefault("window_id")
            ?? throw new CliException("session-required", "The invoking pane has no window.");
    }

    private static string? Field(Client client, string name) => client.RawFormatFields.GetValueOrDefault(name);
    private bool Eligible(Client client) => !client.IsControlClient && !string.IsNullOrEmpty(client.Tty)
        && Field(client, "window_id") == _window && Field(client, "pane_id") == _pane!.Id.ToString()
        && int.TryParse(Field(client, "client_pid"), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0
        && long.TryParse(Field(client, "client_created"), NumberStyles.None, CultureInfo.InvariantCulture, out long created) && created > 0;
    private void RefuseIndependent(IEnumerable<Client> clients)
    {
        if (clients.Any(client => !client.IsControlClient && Field(client, "window_id") == _window && (Field(client, "client_flags") ?? "").Split(',').Contains("active-pane", StringComparer.Ordinal)))
            throw new CliException("independent-pane", "A client on this window has independent active-pane focus. Use -d or --append.");
    }

    internal async Task CompleteAsync(Session target)
    {
        if (Mode is LoadMode.Detached or LoadMode.Append) return;
        if (TerminalInput.RequireForeground(context) != _tty)
            throw new CliException("terminal-changed", "The invoking terminal changed before attachment.");
        if (Mode == LoadMode.Switch)
        {
            if (target.Generation != CurrentSession!.Generation)
                throw new StaleServerGenerationException("The workspace server changed before handoff.", CurrentSession.Generation, target.Generation);
            Pane current = await _pane!.RefreshAsync(context.CancellationToken).ConfigureAwait(false);
            if (current.RawFormatFields.GetValueOrDefault("pane_tty") != _tty || current.RawFormatFields.GetValueOrDefault("window_id") != _window)
                throw new CliException("pane-changed", "The invoking pane changed before handoff.");
            IReadOnlyList<Client> clients = await Server!.GetClientsStrictAsync(context.CancellationToken).ConfigureAwait(false);
            RefuseIndependent(clients);
            Client? selected = clients.FirstOrDefault(client => client.Name == _client!.Name);
            string[] identity = ["client_pid", "client_created", "client_tty", "session_id", "window_id", "pane_id"];
            if (selected is null || !Eligible(selected) || identity.Any(field => string.IsNullOrEmpty(Field(selected, field)) || Field(selected, field) != Field(_client!, field)))
                throw new CliException("client-changed", "The selected client changed before handoff.");
            TmuxCommandResult result = await target.ExecuteCommandAsync(["switch-client", "-c", selected.Name], cancellationToken: context.CancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new CliException("attach-failed", "tmux could not switch the selected client.", result.ExitCode);
        }
        else
        {
            int status = await new ProcessCommands(context, invocation, output).AttachAsync(target).ConfigureAwait(false);
            if (status != 0) throw new CliException("attach-failed", $"tmux attachment exited with status {status}.", status);
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
                throw new CliException("terminal-required", "Attachment requires terminal input and output. Use -d to load without attaching.");
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new CliException("terminal-unsupported", "Native terminal handoff currently requires Linux x64. Use -d.");
            if (ForegroundGroup(0) != ProcessGroup()) throw new CliException("terminal-required", "Attachment requires the foreground controlling terminal. Use -d.");
            return Name();
        }

        private static unsafe string Name()
        {
            byte* bytes = stackalloc byte[4096];
            if (TerminalName(0, bytes, 4096) != 0) throw new CliException("terminal-required", "Cannot identify the input terminal. Use -d.");
            return Marshal.PtrToStringUTF8((nint)bytes)!;
        }

        internal static SafeFileHandle Open(CliContext context)
        {
            RequireForeground(context);
            const int noControllingTerminal = 0x100, nonBlocking = 0x800, closeOnExec = 0x80000;
            SafeFileHandle handle = OpenFile("/dev/tty", noControllingTerminal | nonBlocking | closeOnExec);
            if (!handle.IsInvalid) return handle;
            handle.Dispose();
            throw new CliException("terminal-required", "Cannot read the controlling terminal. Use -d.");
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
                    if (line.Length == 128) throw new CliException("invalid-choice", "The terminal answer is too long.", 2);
                    if (buffer[0] != '\r') line.Append((char)buffer[0]);
                }
                else if (count == 0) throw new CliException("input-closed", "Terminal input ended before a load mode was selected.", 2);
                else
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error is not 4 and not 11) throw new CliException("input-failed", "Cannot read terminal input.");
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
