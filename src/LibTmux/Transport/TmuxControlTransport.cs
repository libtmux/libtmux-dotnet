using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace LibTmux.Internal;

/// <summary>Sends one-shot commands over the control client a server's connections share.</summary>
/// <remarks>
/// <para>
/// A command that tmux can answer in order, without blocking and without
/// choosing a client, goes over the control client as one line and comes back
/// as the same <see cref="TmuxCommandResult" /> a process would have produced.
/// Everything else runs on a process, exactly as before: commands that wait
/// for another client, start or end a server or a session, read or write a
/// file for the client, or open interface on a client.
/// </para>
/// <para>
/// A command that acts on "the current client" when it is given none would,
/// with the control client attached, act on the control client. Such a command
/// is given the most recently active client that is not ours, or fails with
/// tmux's own "no current client" when there is none, which is what it did
/// before the control client existed.
/// </para>
/// <para>
/// When the control client ends while a command is in flight, the command is
/// repeated on a process only when that cannot run it twice: it never reached
/// the write, or it only reads. Any other command fails with
/// <see cref="TmuxDispatchState.Unknown" />.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed class TmuxControlTransport
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["wait"] = "wait-for",
        ["start"] = "start-server",
        ["new"] = "new-session",
        ["attach"] = "attach-session",
        ["run"] = "run-shell",
        ["if"] = "if-shell",
        ["killp"] = "kill-pane",
        ["killw"] = "kill-window",
        ["unlinkw"] = "unlink-window",
        ["movew"] = "move-window",
        ["joinp"] = "join-pane",
        ["movep"] = "move-pane",
        ["saveb"] = "save-buffer",
        ["loadb"] = "load-buffer",
        ["source"] = "source-file",
        ["detach"] = "detach-client",
        ["switchc"] = "switch-client",
        ["lockc"] = "lock-client",
        ["suspendc"] = "suspend-client",
        ["refresh"] = "refresh-client",
        ["displayp"] = "display-panes",
        ["popup"] = "display-popup",
        ["menu"] = "display-menu",
        ["confirm"] = "confirm-before",
        ["capturep"] = "capture-pane",
        ["display"] = "display-message",
        ["has"] = "has-session",
        ["ls"] = "list-sessions",
        ["lsw"] = "list-windows",
        ["lsp"] = "list-panes",
        ["lsc"] = "list-clients",
        ["lsb"] = "list-buffers",
        ["lscm"] = "list-commands",
        ["show"] = "show-options",
        ["showw"] = "show-window-options",
        ["showenv"] = "show-environment",
        ["showb"] = "show-buffer",
        ["showmsgs"] = "show-messages",
    };

    // Commands that must run on a process, and why: they wait for another
    // client (the control client's queue would stall behind them), start or
    // end a server or session (the client goes with it), read or write a file
    // or the client's stdout, or open interface on a client.
    private static readonly HashSet<string> OnProcess = new(StringComparer.Ordinal)
    {
        "wait-for", "start-server", "new-session", "attach-session", "kill-server", "kill-session",
        "kill-window", "kill-pane", "unlink-window", "move-window", "join-pane", "move-pane", "run-shell",
        "save-buffer", "load-buffer", "source-file", "choose-tree", "choose-buffer", "choose-client",
        "display-panes", "display-popup", "display-menu", "command-prompt", "confirm-before",
        "detach-client", "switch-client", "lock-client", "suspend-client", "refresh-client",
    };

    // Commands after which the client may have lost its session or server.
    private static readonly HashSet<string> EndsSessions = new(StringComparer.Ordinal)
    {
        "kill-server", "kill-session", "kill-window", "kill-pane", "unlink-window", "move-window",
        "join-pane", "move-pane", "detach-client",
    };

    private static readonly Dictionary<string, ClientCommand> ClientCommands = new(StringComparer.Ordinal)
    {
        ["detach-client"] = new("t", "Est"),
        ["switch-client"] = new("c", "ctT"),
        ["lock-client"] = new("t", "t"),
        ["suspend-client"] = new("t", "t"),
        ["refresh-client"] = new("t", "ABCfFrt"),
        ["display-panes"] = new("t", "dt"),
        ["display-popup"] = new("c", "bcdehsStTwxy"),
        ["display-menu"] = new("c", "bcCHsStTxy"),
        ["command-prompt"] = new("t", "IpTt"),
        ["confirm-before"] = new("t", "cpt"),

        // These default to the current client only for some of their uses.
        // Without a client of its own to show on, a message has nowhere to go
        // and a buffer is set without being copied out.
        ["display-message"] = new("c", "cdFtxy", NoClient.Nothing, static (command, spec) => !spec.Has(command, 'p'), "3.3"),
        ["set-buffer"] = new("t", "btn", NoClient.WithoutFlag('w'), static (command, spec) => spec.Has(command, 'w')),
        ["send-keys"] = new("c", "cNt", NoClient.Nothing, static (command, spec) => spec.Has(command, 'K')),
    };

    private static readonly HashSet<string> Reads = new(StringComparer.Ordinal)
    {
        "list-sessions", "list-windows", "list-panes", "list-clients", "list-buffers", "list-keys",
        "list-commands", "show-options", "show-window-options", "show-environment", "show-hooks",
        "show-buffer", "show-messages", "has-session", "capture-pane", "display-message",
    };

    private static readonly string[] Named = [.. OnProcess, .. Reads];

    private readonly Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> _process;
    private readonly TmuxControlClient _client;
    private readonly Func<ControlModeSession> _start;
    private readonly Func<TmuxVersion?> _version;

    internal TmuxControlTransport(
        Func<TmuxCommandRequest, CancellationToken, Task<TmuxCommandResult>> process,
        TmuxControlClient client,
        Func<ControlModeSession> start,
        Func<TmuxVersion?>? version = null)
    {
        _process = process;
        _client = client;
        _start = start;
        _version = version ?? (static () => null);
    }

    /// <summary>Gets whether one-shot commands use the control client unless a connection opts out.</summary>
    /// <remarks>
    /// <c>LIBTMUX_CONTROL_TRANSPORT=0</c> turns it off for the process, which is
    /// how the same suite runs against the process transport.
    /// </remarks>
    internal static bool EnabledByDefault =>
        Environment.GetEnvironmentVariable("LIBTMUX_CONTROL_TRANSPORT") != "0";

    internal async Task<TmuxCommandResult> ExecuteAsync(
        TmuxCommandRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyList<string>> commands = request.Commands;
        if (commands.Any(static command => IsOnProcess(command)))
        {
            return await RunOnProcessAsync(request, cancellationToken).ConfigureAwait(false);
        }

        ControlModeSession? session = await _client
            .GetAsync(_start, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return await RunOnProcessAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var probe = new ControlModeSendProbe();
        try
        {
            IReadOnlyList<string> lines = await session
                .SendRawAsync(commands, probe, cancellationToken)
                .ConfigureAwait(false);
            return Result(request, 0, lines, []);
        }
        catch (ControlModeCommandException error)
        {
            // tmux writes a failed command's message after whatever the commands
            // before it printed, in one block, so the last line is the message.
            List<string> all = [.. error.OutputLines, .. error.ErrorLines];
            return Result(request, 1, [.. all.Take(all.Count - 1)], all.Count == 0 ? [] : [all[^1]]);
        }
        catch (ControlModeReplyLimitException error)
        {
            // The client is fine and went on; only this command's answer was too
            // large to keep. A read can be asked of a process, which has its own
            // bound; a command that changes things already ran.
            if (commands.All(static command => IsRead(command)))
            {
                return await RunOnProcessAsync(request, cancellationToken).ConfigureAwait(false);
            }

            throw new TmuxTransportException(
                $"{error.Message} The command ran; its answer was dropped.",
                request.LogicalArguments,
                TmuxDispatchState.Dispatched,
                error);
        }
        catch (ArgumentException)
        {
            // The line is larger than the control client accepts; a process
            // takes the arguments as they are.
            return await RunOnProcessAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException
            && (error is InvalidOperationException or TmuxProtocolException or IOException or ObjectDisposedException))
        {
            if (!probe.MayHaveRun || commands.All(static command => IsRead(command)))
            {
                return await RunOnProcessAsync(request, cancellationToken).ConfigureAwait(false);
            }

            throw new TmuxTransportException(
                "The tmux control client ended while the command was in flight; tmux may have run it.",
                request.LogicalArguments,
                TmuxDispatchState.Unknown,
                error);
        }
    }

    private async Task<TmuxCommandResult> RunOnProcessAsync(
        TmuxCommandRequest request,
        CancellationToken cancellationToken)
    {
        TmuxCommandRequest sent = request;
        if (_client.Own is { } own && request.Commands.Any(static c => ClientCommandOf(c) is not null))
        {
            IReadOnlyList<IReadOnlyList<string>>? pointed = await PointAtAnotherClientAsync(request, own, cancellationToken)
                .ConfigureAwait(false);
            if (pointed is null)
            {
                // What tmux says when no client but ours is attached to answer.
                return Result(request, 1, [], ["no current client"]);
            }

            if (pointed.Count == 0)
            {
                // Every other client was the one to keep: nothing to detach.
                return Result(request, 0, [], []);
            }

            sent = TmuxCommandRequest.Group(request.PreventServerStart, [.. pointed]);
        }

        TmuxCommandResult result = await _process(sent, cancellationToken).ConfigureAwait(false);
        if (_client.Own is not null && request.Commands.Any(static c => EndsSessions.Contains(Canonical(c[0]))))
        {
            // tmux has ended the client's session or server by the time the
            // command answers, but the client learns it a moment later. Ask it
            // something: it either answers or is seen to have ended, so the
            // next command starts from one or the other, not from between.
            await _client.SettleAsync(cancellationToken).ConfigureAwait(false);
        }

        if (result.ExitCode == 0
            && request.Commands.Any(static c => Canonical(c[0]) is "new-session" or "start-server" or "list-sessions"
                or "has-session" or "source-file"))
        {
            _client.MayHaveSession();
        }

        return result;
    }

    // A client command with no client named acts on whichever client tmux
    // finds, which with ours attached is ours. Name the most recently active
    // client that is not ours instead; with none, nothing is left to act on.
    // detach-client -a and -s would detach ours with the rest, so they detach
    // the others one by one.
    private async Task<IReadOnlyList<IReadOnlyList<string>>?> PointAtAnotherClientAsync(
        TmuxCommandRequest request,
        OwnControlClient own,
        CancellationToken cancellationToken)
    {
        List<IReadOnlyList<string>> pointed = [];
        IReadOnlyList<OtherClient>? others = null;
        foreach (IReadOnlyList<string> command in request.Commands)
        {
            ClientCommand? spec = ClientCommandOf(command);
            if (spec is null)
            {
                pointed.Add(command);
                continue;
            }

            Detach? detach = Canonical(command[0]) == "detach-client" ? Detach.Parse(command) : null;
            if (detach is { All: false, Session: null })
            {
                detach = null;
            }

            if (detach is null && spec.NamesClient(command))
            {
                pointed.Add(command);
                continue;
            }

            if (spec.Since is { } since
                && (_version() is not { } running || running < TmuxVersion.Parse(since)))
            {
                // This tmux cannot be told which client; it acts on the current one.
                pointed.Add(command);
                continue;
            }

            others ??= await ListOtherClientsAsync(own, cancellationToken).ConfigureAwait(false);
            if (others.Count == 0)
            {
                if (spec.Alone.Drop)
                {
                    continue;
                }

                if (spec.Alone.Strip is { } stripped)
                {
                    pointed.Add(Without(command, stripped));
                    continue;
                }

                return null;
            }

            OtherClient latest = others.MaxBy(static client => client.Activity)!;
            if (detach is null)
            {
                pointed.Add([command[0], $"-{spec.Flag}", latest.Name, .. command.Skip(1)]);
                continue;
            }

            string? sessionId = null;
            if (!detach.All && detach.Session is { } named)
            {
                sessionId = await ResolveSessionIdAsync(named, cancellationToken).ConfigureAwait(false);
                if (sessionId is null)
                {
                    // Let tmux say what it says of a session it cannot find.
                    pointed.Add([command[0], "-t", latest.Name, .. command.Skip(1)]);
                    continue;
                }
            }

            string kept = detach.Client ?? latest.Name;
            foreach (OtherClient other in others)
            {
                bool detached = detach.All
                    ? !string.Equals(other.Name, kept, StringComparison.Ordinal)
                    : string.Equals(other.SessionId, sessionId, StringComparison.Ordinal);
                if (detached)
                {
                    pointed.Add(detach.For(other.Name));
                }
            }
        }

        return pointed;
    }

    private async Task<IReadOnlyList<OtherClient>> ListOtherClientsAsync(
        OwnControlClient own,
        CancellationToken cancellationToken)
    {
        ControlModeSession? session = await _client.GetAsync(_start, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return [];
        }

        IReadOnlyList<string> rows = await session
            .SendRawAsync(
                [["list-clients", "-F", "#{client_activity}\t#{client_name}\t#{session_id}"]],
                new ControlModeSendProbe(),
                cancellationToken)
            .ConfigureAwait(false);
        List<OtherClient> others = [];
        foreach (string row in rows)
        {
            string[] parts = row.Split('\t', 3);
            if (parts.Length == 3
                && !string.Equals(parts[1], own.Name, StringComparison.Ordinal)
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long activity))
            {
                others.Add(new OtherClient(parts[1], activity, parts[2]));
            }
        }

        return others;
    }

    private async Task<string?> ResolveSessionIdAsync(string target, CancellationToken cancellationToken)
    {
        ControlModeSession? session = await _client.GetAsync(_start, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        try
        {
            IReadOnlyList<string> rows = await session
                .SendRawAsync(
                    [["display-message", "-p", "-t", target, "#{session_id}"]],
                    new ControlModeSendProbe(),
                    cancellationToken)
                .ConfigureAwait(false);
            return rows.Count == 1 && rows[0].Length > 0 ? rows[0] : null;
        }
        catch (ControlModeCommandException)
        {
            return null;
        }
    }

    private sealed record OtherClient(string Name, long Activity, string SessionId);

    /// <summary>The parts of a detach-client that decide which clients it detaches.</summary>
    private sealed record Detach(bool All, string? Session, string? Client, string? Command, bool Print)
    {
        internal static Detach Parse(IReadOnlyList<string> command)
        {
            bool all = false;
            bool print = false;
            string? session = null;
            string? client = null;
            string? shell = null;
            for (int index = 1; index < command.Count; index++)
            {
                string word = command[index];
                if (word == "--" || word.Length < 2 || word[0] != '-')
                {
                    break;
                }

                for (int at = 1; at < word.Length; at++)
                {
                    char flag = word[at];
                    if (flag == 'a')
                    {
                        all = true;
                    }
                    else if (flag == 'P')
                    {
                        print = true;
                    }
                    else if (flag is 'E' or 's' or 't')
                    {
                        string? value = at < word.Length - 1
                            ? word[(at + 1)..]
                            : index + 1 < command.Count ? command[++index] : null;
                        if (flag == 'E')
                        {
                            shell = value;
                        }
                        else if (flag == 's')
                        {
                            session = value;
                        }
                        else
                        {
                            client = value;
                        }

                        break;
                    }
                }
            }

            return new Detach(all, session, client, shell, print);
        }

        internal List<string> For(string client)
        {
            List<string> detach = ["detach-client", "-t", client];
            if (Print)
            {
                detach.Add("-P");
            }

            if (Command is not null)
            {
                detach.AddRange(["-E", Command]);
            }

            return detach;
        }
    }

    private static TmuxCommandResult Result(
        TmuxCommandRequest request,
        int exitCode,
        IReadOnlyList<string> output,
        IReadOnlyList<string> error)
    {
        byte[] outputBytes = Encode(output);
        byte[] errorBytes = Encode(error);
        return new TmuxCommandResult(
            request.LogicalArguments,
            exitCode,
            outputBytes,
            errorBytes,
            Utf8BackslashDecoder.ProjectOutputLines(outputBytes),
            Utf8BackslashDecoder.ProjectErrorLines(errorBytes));
    }

    private static byte[] Encode(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? [] : Encoding.UTF8.GetBytes(string.Concat(lines.Select(static line => line + "\n")));

    /// <summary>Resolves a command word, which tmux lets be an alias or a unique prefix, to its full name.</summary>
    private static string Canonical(string word)
    {
        if (Aliases.TryGetValue(word, out string? full))
        {
            return full;
        }

        if (word.Length < 2)
        {
            return word;
        }

        // A prefix of a command this transport treats specially is treated as
        // that command; tmux rejects an ambiguous one either way.
        string[] matches = [.. Named.Where(name => name.StartsWith(word, StringComparison.Ordinal))];
        return matches.Length == 1 ? matches[0] : word;
    }

    private static bool IsOnProcess(IReadOnlyList<string> command)
    {
        string name = Canonical(command[0]);
        if (OnProcess.Contains(name) || ClientCommandOf(command) is not null)
        {
            return true;
        }

        // A shell condition runs in the foreground and holds the client's queue.
        return name == "if-shell" && !command.Skip(1).TakeWhile(static a => a.StartsWith('-') && a != "--")
            .Any(static a => a.Contains('F', StringComparison.Ordinal));
    }

    private static ClientCommand? ClientCommandOf(IReadOnlyList<string> command) =>
        ClientCommands.TryGetValue(Canonical(command[0]), out ClientCommand? spec)
            && (spec.Applies is null || spec.Applies(command, spec))
            ? spec
            : null;

    private static bool IsRead(IReadOnlyList<string> command)
    {
        string name = Canonical(command[0]);

        // The generation guard in front of an entity's commands runs a command
        // only when the generation is not the expected one, and that command is
        // not one tmux knows, so it changes nothing.
        if (name == "if-shell" && command is [_, "-F", _, "", _])
        {
            return true;
        }

        if (!Reads.Contains(name))
        {
            return false;
        }

        // Both print only with -p; without it they write a buffer or a message.
        return name is not ("capture-pane" or "display-message")
            || command.Skip(1).TakeWhile(static a => a.StartsWith('-') && a != "--")
                .Any(static a => a.Contains('p', StringComparison.Ordinal));
    }

    /// <summary>What a client command does when only the library's client is attached.</summary>
    /// <param name="Drop">Leave the command out: it has nothing to act on and succeeds without effect.</param>
    /// <param name="Strip">A flag to take out, leaving the rest to run.</param>
    private sealed record NoClient(bool Drop, char? Strip)
    {
        internal static NoClient Fail { get; } = new(false, null);

        internal static NoClient Nothing { get; } = new(true, null);

        internal static NoClient WithoutFlag(char flag) => new(false, flag);
    }

    /// <summary>How a command names its client, and which flags carry a value.</summary>
    /// <param name="Flag">The flag that names the client.</param>
    /// <param name="ValueFlags">The flags that take a value.</param>
    /// <param name="WhenAlone">What to do when no client but ours is attached, or null to fail.</param>
    /// <param name="Applies">Whether this use of the command defaults to the current client, or null for every use.</param>
    /// <param name="Since">The first tmux that accepts a client flag on this command, or null for every version.</param>
    private sealed record ClientCommand(
        string Flag,
        string ValueFlags,
        NoClient? WhenAlone = null,
        Func<IReadOnlyList<string>, ClientCommand, bool>? Applies = null,
        string? Since = null)
    {
        internal NoClient Alone => WhenAlone ?? NoClient.Fail;

        internal bool NamesClient(IReadOnlyList<string> command) => Scan(command, Flag);

        internal bool Has(IReadOnlyList<string> command, char wanted) =>
            Scan(command, wanted.ToString());

        private bool Scan(IReadOnlyList<string> command, string wanted)
        {
            for (int index = 1; index < command.Count; index++)
            {
                string word = command[index];
                if (word == "--" || word.Length < 2 || word[0] != '-')
                {
                    return false;
                }

                for (int at = 1; at < word.Length; at++)
                {
                    char flag = word[at];
                    if (wanted.Contains(flag, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (ValueFlags.Contains(flag, StringComparison.Ordinal))
                    {
                        // The value is the rest of the word, or the next word.
                        if (at == word.Length - 1)
                        {
                            index++;
                        }

                        break;
                    }
                }
            }

            return false;
        }
    }

    private static List<string> Without(IReadOnlyList<string> command, char flag)
    {
        List<string> kept = [command[0]];
        bool flags = true;
        for (int index = 1; index < command.Count; index++)
        {
            string word = command[index];
            if (flags && word == "--")
            {
                flags = false;
            }
            else if (flags && word.Length >= 2 && word[0] == '-')
            {
                string remaining = word.Replace(flag.ToString(), string.Empty, StringComparison.Ordinal);
                if (remaining.Length > 1)
                {
                    kept.Add(remaining);
                }

                continue;
            }
            else
            {
                flags = false;
            }

            kept.Add(word);
        }

        return kept;
    }
}
