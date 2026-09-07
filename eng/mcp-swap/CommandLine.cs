namespace LibTmux.McpSwap;

internal enum SwapCommand
{
    Detect,
    Status,
    Use,
    Revert,
    Doctor,
    Help,
}

internal enum SourceKind
{
    Debug,
    Release,
    Run,
    Path,
    Published,
}

internal sealed record CommandOptions
{
    internal SwapCommand Command { get; init; }

    internal SourceKind Source { get; init; } = SourceKind.Debug;

    internal string Repository { get; init; } = ".";

    internal string Project { get; init; } = "LibTmux.Mcp";

    internal string? ServerName { get; init; }

    internal string? EntryName { get; init; }

    internal string? Version { get; init; }

    internal string? BinaryPath { get; init; }

    internal ConfigScope? Scope { get; init; }

    internal IReadOnlyList<string> Clients { get; init; } = [];

    internal IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    internal bool DryRun { get; init; }

    internal bool NoBuild { get; init; }

    internal bool NoPreflight { get; init; }
}

internal sealed class CommandLineException(string message) : Exception(message);

internal static class CommandLine
{
    internal static CommandOptions Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            throw new CommandLineException("expected detect, status, use, revert, or doctor");
        }

        if (arguments.Count == 1 && arguments[0] is "help" or "-h" or "--help")
        {
            return new() { Command = SwapCommand.Help };
        }

        SwapCommand command = arguments[0] switch
        {
            "detect" => SwapCommand.Detect,
            "status" => SwapCommand.Status,
            "use" => SwapCommand.Use,
            "revert" => SwapCommand.Revert,
            "doctor" => SwapCommand.Doctor,
            _ => throw new CommandLineException($"unknown command {arguments[0]}")
        };
        MutableOptions parsed = new(command);
        for (int index = 1; index < arguments.Count; index++)
        {
            string token = arguments[index];
            (string name, string? inlineValue) = SplitFlag(token);
            if (name == "--dry-run")
            {
                RequireCommand(command, name, SwapCommand.Use, SwapCommand.Revert);
                RequireNoValue(name, inlineValue);
                parsed.DryRun = true;
            }
            else if (name == "--no-build")
            {
                RequireCommand(command, name, SwapCommand.Use);
                RequireNoValue(name, inlineValue);
                parsed.NoBuild = true;
            }
            else if (name == "--no-preflight")
            {
                RequireCommand(command, name, SwapCommand.Use);
                RequireNoValue(name, inlineValue);
                parsed.NoPreflight = true;
            }
            else
            {
                string value = inlineValue ?? NextValue(arguments, ref index, name);
                Assign(parsed, command, name, value);
            }
        }

        Validate(parsed);
        return parsed.Freeze();
    }

    internal static string Usage =>
        "usage: mcp-swap detect|status|use|revert|doctor [options]";

    private static void Assign(MutableOptions options, SwapCommand command, string name, string value)
    {
        switch (name)
        {
            case "--repo":
                RequireCommand(command, name, SwapCommand.Status, SwapCommand.Use, SwapCommand.Doctor);
                options.Repository = value;
                break;
            case "--server":
                RequireCommand(command, name, SwapCommand.Status, SwapCommand.Use, SwapCommand.Doctor);
                options.ServerName = value;
                break;
            case "--cli":
                RequireCommand(command, name, SwapCommand.Status, SwapCommand.Use, SwapCommand.Revert);
                options.Clients.Add(value);
                break;
            case "--scope":
                RequireCommand(command, name, SwapCommand.Status, SwapCommand.Use, SwapCommand.Revert);
                options.Scope = value switch
                {
                    "user" => ConfigScope.User,
                    "project" => ConfigScope.Project,
                    _ => throw new CommandLineException("--scope expects user or project"),
                };
                break;
            case "--source":
                RequireCommand(command, name, SwapCommand.Use);
                options.Source = value switch
                {
                    "debug" => SourceKind.Debug,
                    "release" => SourceKind.Release,
                    "run" => SourceKind.Run,
                    "path" => SourceKind.Path,
                    "published" => SourceKind.Published,
                    _ => throw new CommandLineException(
                        "--source expects debug, release, run, path, or published"),
                };
                break;
            case "--version":
                RequireCommand(command, name, SwapCommand.Use);
                options.Version = value;
                break;
            case "--bin":
                RequireCommand(command, name, SwapCommand.Use);
                options.BinaryPath = value;
                break;
            case "--project":
                RequireCommand(command, name, SwapCommand.Use);
                options.Project = value;
                break;
            case "--entry":
                RequireCommand(command, name, SwapCommand.Use);
                options.EntryName = value;
                break;
            case "--env":
                RequireCommand(command, name, SwapCommand.Use);
                int separator = value.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    throw new CommandLineException("--env expects KEY=VALUE");
                }

                string variable = value[..separator];
                if (string.Equals(variable, "LIBTMUX_SAFETY", StringComparison.Ordinal))
                {
                    throw new CommandLineException(
                        "LIBTMUX_SAFETY has been removed; select capabilities with LIBTMUX_TOOLSETS");
                }

                options.Environment[variable] = value[(separator + 1)..];
                break;
            default:
                throw new CommandLineException($"unknown option {name}");
        }
    }

    private static void Validate(MutableOptions options)
    {
        if (options.Command != SwapCommand.Use)
        {
            return;
        }

        if (!SourceResolver.IsSafePathComponent(options.Project)
            || (options.EntryName is not null
                && !SourceResolver.IsSafePathComponent(options.EntryName)))
        {
            throw new CommandLineException("--project and --entry must each be one safe path component");
        }

        if (options.Source == SourceKind.Path && string.IsNullOrEmpty(options.BinaryPath))
        {
            throw new CommandLineException("--source path needs --bin");
        }

        if (options.Source == SourceKind.Published && string.IsNullOrEmpty(options.Version))
        {
            throw new CommandLineException("--source published needs --version");
        }

        if (options.Source == SourceKind.Published
            && !SourceResolver.IsSafePublishedVersion(options.Version!))
        {
            throw new CommandLineException("--version must be one safe exact version");
        }

        if (options.Source != SourceKind.Path && options.BinaryPath is not null)
        {
            throw new CommandLineException("--bin only means something with --source path");
        }

        if (options.Source != SourceKind.Published && options.Version is not null)
        {
            throw new CommandLineException("--version only means something with --source published");
        }

        if (options.NoBuild && options.Source is not SourceKind.Debug and not SourceKind.Release)
        {
            throw new CommandLineException("--no-build only means something with debug or release");
        }
    }

    private static (string Name, string? Value) SplitFlag(string token)
    {
        if (!token.StartsWith("--", StringComparison.Ordinal))
        {
            throw new CommandLineException($"unexpected argument {token}");
        }

        int separator = token.IndexOf('=', StringComparison.Ordinal);
        return separator < 0 ? (token, null) : (token[..separator], token[(separator + 1)..]);
    }

    private static string NextValue(IReadOnlyList<string> arguments, ref int index, string name)
    {
        index++;
        if (index >= arguments.Count || arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CommandLineException($"{name} expects a value");
        }

        return arguments[index];
    }

    private static void RequireNoValue(string name, string? value)
    {
        if (value is not null)
        {
            throw new CommandLineException($"{name} does not take a value");
        }
    }

    private static void RequireCommand(
        SwapCommand actual,
        string option,
        params SwapCommand[] accepted)
    {
        if (!accepted.Contains(actual))
        {
            throw new CommandLineException($"{option} does not apply to {actual.ToString().ToLowerInvariant()}");
        }
    }

    private sealed class MutableOptions(SwapCommand command)
    {
        internal SwapCommand Command { get; } = command;

        internal SourceKind Source { get; set; } = SourceKind.Debug;

        internal string Repository { get; set; } = ".";

        internal string Project { get; set; } = "LibTmux.Mcp";

        internal string? ServerName { get; set; }

        internal string? EntryName { get; set; }

        internal string? Version { get; set; }

        internal string? BinaryPath { get; set; }

        internal ConfigScope? Scope { get; set; }

        internal List<string> Clients { get; } = [];

        internal Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

        internal bool DryRun { get; set; }

        internal bool NoBuild { get; set; }

        internal bool NoPreflight { get; set; }

        internal CommandOptions Freeze() => new()
        {
            Command = Command,
            Source = Source,
            Repository = Repository,
            Project = Project,
            ServerName = ServerName,
            EntryName = EntryName,
            Version = Version,
            BinaryPath = BinaryPath,
            Scope = Scope,
            Clients = Clients.ToArray(),
            Environment = new Dictionary<string, string>(Environment, StringComparer.Ordinal),
            DryRun = DryRun,
            NoBuild = NoBuild,
            NoPreflight = NoPreflight,
        };
    }
}
