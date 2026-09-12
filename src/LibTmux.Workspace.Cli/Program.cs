using System.Collections;
using System.CommandLine;

namespace LibTmux.Workspace.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler interrupt = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += interrupt;
        try { return await CliRunner.RunAsync(args, Console.Out, Console.Error, cancellationToken: cancellation.Token).ConfigureAwait(false); }
        finally { Console.CancelKeyPress -= interrupt; }
    }
}

internal static class CliRunner
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string? directory = null, IReadOnlyDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default)
    {
        environment ??= Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.Ordinal);
        CliContext context = new(output, error, Path.GetFullPath(directory ?? Environment.CurrentDirectory), environment, cancellationToken);
        CommandLine graph = new();
        Invocation invocation = graph.Parse(args);
        Output renderer = new(context, invocation);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (invocation.Errors.Count > 0) throw new CliException("usage", string.Join("\n", invocation.Errors), 2);
            if (invocation.Text("generate") is string format)
            {
                if (invocation.Machine) renderer.Result(format == "reference" ? graph.Describe() : new { format, content = CommandDocumentation.Generate(graph, format) });
                else output.Write(CommandDocumentation.Generate(graph, format));
                return 0;
            }
            if (invocation.Flag("version"))
            {
                string version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(CliRunner).Assembly)?.InformationalVersion.Split('+')[0] ?? "unknown";
                renderer.Result(new { name = "tmux-workspace", version }, "tmux-workspace " + version);
                return 0;
            }
            if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal) || invocation.Command is "" or "import")
            {
                using StringWriter help = new();
                ParseResult parsed = args.Length == 0 || invocation.Command == "import" ? graph.Root.Parse([.. args, "--help"]) : invocation.Parsed;
                parsed.Invoke(new InvocationConfiguration { Output = help, Error = error });
                renderer.Human(help.ToString(), "heading", newline: false);
                return 0;
            }
            if (invocation.Text("log_file") is string path)
            {
                TextWriter log = LogFile.Open(Path.GetFullPath(path, context.Directory));
                await renderer.DisposeAsync().ConfigureAwait(false);
                renderer = new Output(context, invocation, log);
            }
            ReadCommands commands = new(context, invocation, renderer);
            switch (invocation.Command)
            {
                case "ls": commands.List(); break;
                case "search": commands.Search(); break;
                case "convert": commands.Convert(); break;
                case "import teamocil": commands.Import(true); break;
                case "import tmuxinator": commands.Import(false); break;
                case "load" when !OperatingSystem.IsWindows(): return await new ExecutionCommands(context, invocation, renderer).LoadAsync().ConfigureAwait(false);
                case "freeze" when !OperatingSystem.IsWindows(): await new ExecutionCommands(context, invocation, renderer).FreezeAsync().ConfigureAwait(false); break;
                case "load" or "freeze": throw new CliException("unsupported-platform", "Native tmux workspace execution requires Unix.");
                case "edit": return await new ProcessCommands(context, invocation, renderer).EditAsync().ConfigureAwait(false);
                case "shell": return await new ProcessCommands(context, invocation, renderer).ShellAsync().ConfigureAwait(false);
                case "debug-info": await new ProcessCommands(context, invocation, renderer).DebugAsync().ConfigureAwait(false); break;
            }
            return 0;
        }
        catch (OperationCanceledException) { await renderer.DiagnosticAsync("cancelled", "Operation cancelled.").ConfigureAwait(false); return 130; }
        catch (CliException failure) { await renderer.DiagnosticAsync(failure.Code, failure.Message).ConfigureAwait(false); return failure.ExitCode; }
        catch (StaleServerGenerationException failure) { await renderer.DiagnosticAsync("stale-server", failure.Message).ConfigureAwait(false); return 1; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or LibTmuxException)
        {
            await renderer.DiagnosticAsync("operation-failed", failure.Message).ConfigureAwait(false);
            return 1;
        }
        finally { await renderer.DisposeAsync().ConfigureAwait(false); }
    }
}
