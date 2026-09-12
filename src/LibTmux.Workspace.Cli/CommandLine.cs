using System.CommandLine;
using System.CommandLine.Parsing;

namespace LibTmux.Workspace.Cli;

internal sealed class CommandLine
{
    private readonly Dictionary<Command, List<(string Key, Func<ParseResult, object?> Read)>> _readers = [];
    private readonly Dictionary<Command, string> _names = [];
    private readonly List<Argument> _arguments = [];
    private readonly Dictionary<Option, string[]> _choices = [];

    internal CommandLine()
    {
        Root = new RootCommand("Manage tmux workspaces from YAML and JSON.");
        Root.SetAction(_ => 0);
        _names[Root] = "";
        _readers[Root] = [];
        Flag(Root, "json", "--json", "Write a JSON result.", recursive: true);
        Flag(Root, "ndjson", "--ndjson", "Stream newline-delimited JSON.", recursive: true);
        Value(Root, "color", "--color", "When to use semantic colors.", "auto", ["auto", "always", "never"], recursive: true);
        Value(Root, "log_level", "--log-level", "Diagnostic log level.", "warning", ["debug", "info", "warning", "error", "critical"]);
        Flag(Root, "version", "--version", "Show the tool version.", ["-V"]);
        Value(Root, "generate", "--generate", "Generate reference, man, bash, zsh or fish output.", choices: ["reference", "man", "bash", "zsh", "fish"]);

        Command load = Add(Root, "load", "Load one or more workspaces.");
        Arguments(load, "files", "workspace-file", ArgumentArity.OneOrMore);
        Endpoint(load);
        Value(load, "tmux_config", "-f", "Read this tmux configuration file.");
        Value(load, "session_name", "-s", "Override the session name.");
        Yes(load);
        Flag(load, "detached", "-d", "Load without attaching.");
        Flag(load, "append", "--append", "Append windows to the current session.", ["-a"]);
        Flag(load, "colors256", "-2", "Tell tmux the terminal supports 256 colors.");
        Flag(load, "colors88", "-8", "Tell tmux the terminal supports 88 colors.");
        Value(load, "log_file", "--log-file", "Write operation diagnostics to this file.");
        Value(load, "progress_format", "--progress-format", "Progress preset or token template.");
        Option<int?> lines = new("--progress-lines") { Description = "Script panel lines; 0 disables the panel and -1 uses terminal height." };
        load.Options.Add(lines);
        _readers[load].Add(("progress_lines", result => result.GetValue(lines)));
        Flag(load, "no_progress", "--no-progress", "Disable animated progress.");

        Command freeze = Add(Root, "freeze", "Capture a live session as a workspace.");
        Arguments(freeze, "sessions", "session-name", ArgumentArity.ZeroOrOne);
        Endpoint(freeze);
        SaveOptions(freeze, true);
        Yes(freeze);
        Flag(freeze, "quiet", "--quiet", "Suppress explanatory status text.", ["-q"]);
        Command convert = Add(Root, "convert", "Convert a workspace between YAML and JSON.");
        Arguments(convert, "files", "workspace-file", ArgumentArity.ExactlyOne);
        Yes(convert);
        SaveOptions(convert, false);
        Command import = Add(Root, "import", "Import teamocil or tmuxinator configuration.");
        foreach (string name in new[] { "teamocil", "tmuxinator" })
        {
            Command child = Add(import, name, $"Import a {name} configuration.");
            Arguments(child, "files", "workspace-file", ArgumentArity.ExactlyOne);
            SaveOptions(child, false);
            Yes(child);
        }
        Command list = Add(Root, "ls", "List local and global workspace files.");
        Flag(list, "tree", "--tree", "Group workspaces by directory.");
        Flag(list, "full", "--full", "Include complete configuration documents.");
        Command search = Add(Root, "search", "Search workspace names and configuration fields.");
        Arguments(search, "patterns", "query", ArgumentArity.ZeroOrMore);
        Option<string[]> field = new("--field", "-f") { Description = "Restrict matching to name, session/s, path/p, window/w or pane.", AllowMultipleArgumentsPerToken = false };
        search.Options.Add(field);
        _readers[search].Add(("fields", result => result.GetValue(field) ?? []));
        Flag(search, "ignore_case", "--ignore-case", "Ignore letter case.", ["-i"]);
        Flag(search, "smart_case", "--smart-case", "Ignore case for lowercase patterns.", ["-S"]);
        Flag(search, "fixed", "--fixed-strings", "Match literal strings.", ["-F"]);
        Flag(search, "word", "--word-regexp", "Match whole words.", ["-w"]);
        Flag(search, "invert", "--invert-match", "Select workspaces that do not match.", ["-v"]);
        Flag(search, "any", "--any", "Match any pattern instead of every pattern.");
        Command edit = Add(Root, "edit", "Open a workspace in EDITOR.");
        Arguments(edit, "files", "workspace-file", ArgumentArity.ExactlyOne);
        Add(Root, "debug-info", "Report runtime, configuration and tmux diagnostics.");
        Command shell = Add(Root, "shell", "Open a Python shell with tmux objects.");
        Arguments(shell, "sessions", "session-name", ArgumentArity.ZeroOrOne);
        Arguments(shell, "windows", "window-name", ArgumentArity.ZeroOrOne);
        Endpoint(shell);
        Value(shell, "code", "-c", "Evaluate Python code and exit.");
        foreach (string backend in new[] { "best", "pdb", "code", "ptipython", "ptpython", "ipython", "bpython" })
        {
            Flag(shell, "backend_" + backend, "--" + backend, $"Select the {backend} shell.");
        }
        foreach (string option in new[] { "use-pythonrc", "no-startup", "use-vi-mode", "no-vi-mode" })
        {
            Flag(shell, option.Replace('-', '_'), "--" + option, option.Replace('-', ' ') + ".");
        }
    }

    internal RootCommand Root { get; }

    internal Invocation Parse(string[] args)
    {
        ParseResult parsed = Root.Parse(args);
        List<string> errors = parsed.Errors.Select(error => error.Message).ToList();
        foreach (Argument argument in _arguments)
        {
            IReadOnlyList<Token>? tokens = parsed.GetResult(argument)?.Tokens;
            if (tokens is null) continue;
            foreach (Token token in parsed.Tokens.TakeWhile(token => token.Type != TokenType.DoubleDash))
            {
                if (tokens.Contains(token) && token.Value.StartsWith('-') && token.Value != "-")
                {
                    errors.Add($"Unrecognized option '{token.Value}'. Use -- before a filename beginning with a hyphen.");
                }
            }
        }
        Command selected = parsed.CommandResult.Command;
        Dictionary<string, object?> values = [];
        foreach (Command command in Ancestors(selected))
        {
            foreach ((string key, Func<ParseResult, object?> read) in _readers[command])
            {
                try { values[key] = read(parsed); }
                catch (InvalidOperationException) when (parsed.Errors.Count > 0 || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal)) { values[key] = null; }
            }
        }
        Invocation invocation = new(_names[selected], values, args, parsed, errors);
        if (invocation.Flag("colors256") && invocation.Flag("colors88")) errors.Add("-2 and -8 cannot be combined.");
        if (values.Count(pair => pair.Key.StartsWith("backend_", StringComparison.Ordinal) && pair.Value is true) > 1) errors.Add("Choose one Python shell backend.");
        if (values.GetValueOrDefault("progress_lines") is int count && count < -1) errors.Add("--progress-lines must be -1 or greater.");
        return invocation;
    }

    private static IEnumerable<Command> Ancestors(Command command)
    {
        Command? parent = command.Parents.OfType<Command>().FirstOrDefault();
        if (parent is not null) foreach (Command ancestor in Ancestors(parent)) yield return ancestor;
        yield return command;
    }

    private Command Add(Command parent, string name, string description)
    {
        Command command = new(name, description);
        parent.Subcommands.Add(command);
        _readers[command] = [];
        _names[command] = (_names[parent] + " " + name).Trim();
        return command;
    }

    private void Arguments(Command command, string key, string name, ArgumentArity arity)
    {
        if (arity.MaximumNumberOfValues == 1)
        {
            Argument<string?> argument = new(name) { Arity = arity };
            command.Arguments.Add(argument);
            _arguments.Add(argument);
            _readers[command].Add((key, result => result.GetValue(argument) is string value ? new[] { value } : []));
        }
        else
        {
            Argument<string[]> argument = new(name) { Arity = arity };
            command.Arguments.Add(argument);
            _arguments.Add(argument);
            _readers[command].Add((key, result => result.GetValue(argument) ?? []));
        }
    }

    private void Flag(Command command, string key, string name, string description, string[]? aliases = null, bool recursive = false)
    {
        Option<bool> option = new(name, aliases ?? []) { Description = description, Recursive = recursive };
        command.Options.Add(option);
        _readers[command].Add((key, result => result.GetValue(option)));
    }

    private void Value(Command command, string key, string name, string description, string? value = null, string[]? choices = null, string[]? aliases = null, bool recursive = false)
    {
        Option<string?> option = new(name, aliases ?? []) { Description = description, DefaultValueFactory = _ => value, Recursive = recursive };
        if (choices is not null) { option.AcceptOnlyFromAmong(choices); _choices[option] = choices; }
        command.Options.Add(option);
        _readers[command].Add((key, result => result.GetValue(option)));
    }

    private void Endpoint(Command command)
    {
        Value(command, "socket_name", "-L", "Select a named tmux socket.");
        Value(command, "socket_path", "-S", "Select an explicit tmux socket path.");
    }

    private void Yes(Command command) => Flag(command, "yes", "--yes", "Answer yes to confirmation prompts.", ["-y"]);

    private void SaveOptions(Command command, bool freeze)
    {
        Value(command, "workspace_format", "--workspace-format", "Workspace file format.", choices: ["yaml", "json"], aliases: freeze ? ["-f"] : []);
        Value(command, "save_to", "--save-to", "Save the document to this path.", aliases: freeze ? ["-o"] : []);
        Flag(command, "force", "--force", "Replace an existing destination.");
    }

    internal object Describe(Command? command = null)
    {
        command ??= Root;
        return new
        {
            name = command == Root ? "tmux-workspace" : command.Name,
            description = command.Description,
            aliases = command.Aliases,
            arguments = command.Arguments.Select(argument => new { name = argument.Name, type = argument.ValueType.Name, minimum = argument.Arity.MinimumNumberOfValues, maximum = argument.Arity.MaximumNumberOfValues }),
            options = command.Options.Select(option => new { name = option.Name, aliases = option.Aliases, description = option.Description, type = option.ValueType.Name, minimum = option.Arity.MinimumNumberOfValues, maximum = option.Arity.MaximumNumberOfValues, recursive = option.Recursive, required = option.Required, @default = option.HasDefaultValue ? option.GetDefaultValue() : null, choices = _choices.GetValueOrDefault(option) ?? [] }),
            commands = command.Subcommands.Select(child => Describe(child)),
        };
    }
}

internal sealed record Invocation(string Command, Dictionary<string, object?> Values, string[] Arguments, ParseResult Parsed, List<string> Errors)
{
    internal bool Flag(string key) => Values.GetValueOrDefault(key) is true;
    internal string? Text(string key) => Values.GetValueOrDefault(key) as string;
    internal string[] Many(string key) => Values.GetValueOrDefault(key) as string[] ?? [];
    internal bool Machine => Flag("json") || Flag("ndjson");
}
