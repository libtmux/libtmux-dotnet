using System.CommandLine;
using System.Globalization;
using System.Text;

namespace LibTmux.Workspace.Cli;

internal static class CommandDocumentation
{
    internal static string Generate(CommandLine graph, string format)
    {
        StringBuilder result = new();
        void Visit(Command command, string path)
        {
            if (format is "reference" or "man")
            {
                result.AppendLine(format == "man" ? ".SH " + path.ToUpperInvariant() : "## " + path);
                result.AppendLine(command.Description);
                result.AppendLine();
                foreach (Argument argument in command.Arguments) result.AppendLine(CultureInfo.InvariantCulture, $"{argument.Name}: {argument.Arity.MinimumNumberOfValues}..{argument.Arity.MaximumNumberOfValues} values.");
                foreach (Option option in command.Options) result.AppendLine(CultureInfo.InvariantCulture, $"{string.Join(", ", new[] { option.Name }.Concat(option.Aliases))}: {option.Description}");
                result.AppendLine();
            }
            foreach (Command child in command.Subcommands) Visit(child, path + " " + child.Name);
        }
        if (format is "reference" or "man")
        {
            if (format == "man") result.AppendLine(".TH TMUX-WORKSPACE 1");
            Visit(graph.Root, "tmux-workspace");
            return result.ToString();
        }
        return format switch
        {
            "bash" => Bash(graph.Root),
            "zsh" => Zsh(graph.Root),
            "fish" => Fish(graph.Root),
            _ => throw new CliException("invalid_format", "Unknown documentation format.", 2),
        };
    }

    private static IEnumerable<string> FlagNames(Option option) =>
        new[] { option.Name }.Concat(option.Aliases).Distinct(StringComparer.Ordinal);

    // `load --<Tab>` offered every subcommand's flags on every shell, not
    // just load's own -- each generator below scopes its offer to the
    // subcommand path a word actually names, one level of nesting deep
    // (matching this tool's own command tree: a subcommand's subcommand,
    // such as `import teamocil`).

    private static string Fish(Command root)
    {
        static string LongOrShort(string flag) => flag.StartsWith("--", StringComparison.Ordinal)
            ? "-l " + flag[2..]
            : "-s " + flag[1..];
        static string Quoted(string text) => "'" + text.Replace("'", "\\'", StringComparison.Ordinal) + "'";

        StringBuilder result = new();
        void EmitOption(Option option, string condition)
        {
            string flags = string.Join(' ', FlagNames(option).Select(LongOrShort).Distinct(StringComparer.Ordinal));
            string description = option.Description is string text ? " -d " + Quoted(text) : "";
            result.Append("complete -c tmux-workspace -f -n ").Append(Quoted(condition)).Append(' ').Append(flags).Append(description).Append('\n');
        }
        void EmitSubcommandNames(string condition, IEnumerable<string> names)
        {
            string list = string.Join(' ', names);
            result.Append("complete -c tmux-workspace -f -n ").Append(Quoted(condition)).Append(" -a ").Append(Quoted(list)).Append('\n');
        }

        string[] topNames = [.. root.Subcommands.Select(command => command.Name)];
        string noneYet = "not __fish_seen_subcommand_from " + string.Join(' ', topNames);
        EmitSubcommandNames(noneYet, topNames);
        foreach (Option option in root.Options)
        {
            // Recursive options (json/ndjson/color) apply everywhere; the
            // rest of root's own options are usage for the bare command,
            // before any subcommand is chosen.
            EmitOption(option, option.Recursive ? "true" : noneYet);
        }
        foreach (Command command in root.Subcommands)
        {
            string seenTop = "__fish_seen_subcommand_from " + command.Name + "; and ";
            foreach (Option option in command.Options) EmitOption(option, seenTop + "true");
            if (command.Subcommands.Count > 0)
            {
                string[] childNames = [.. command.Subcommands.Select(child => child.Name)];
                EmitSubcommandNames(seenTop + "not __fish_seen_subcommand_from " + string.Join(' ', childNames), childNames);
                foreach (Command child in command.Subcommands)
                {
                    string seenChild = seenTop + "__fish_seen_subcommand_from " + child.Name + "; and true";
                    foreach (Option option in child.Options) EmitOption(option, seenChild);
                }
            }
        }
        return result.ToString();
    }

    // Scans the words already on the line for a recognized subcommand name
    // (and, one level deeper, a recognized name of its own), the same way
    // bash and zsh's own multi-level completions dispatch: by content, not
    // position, so options typed before the subcommand do not confuse it.
    private static string Bash(Command root)
    {
        string[] recursive = [.. root.Options.Where(option => option.Recursive).SelectMany(FlagNames)];
        string rootWords = string.Join(' ', root.Subcommands.Select(command => command.Name).Concat(root.Options.SelectMany(FlagNames)));

        StringBuilder script = new();
        script.Append("_tmux_workspace() {\n");
        script.Append("    local cur=${COMP_WORDS[COMP_CWORD]}\n");
        script.Append("    local cmd sub i j\n");
        script.Append("    for ((i=1; i<COMP_CWORD; i++)); do case \"${COMP_WORDS[i]}\" in\n");
        script.Append("        " + string.Join('|', root.Subcommands.Select(command => command.Name)) + ") cmd=${COMP_WORDS[i]}; break ;;\n");
        script.Append("    esac; done\n");
        foreach (Command parent in root.Subcommands.Where(command => command.Subcommands.Count > 0))
        {
            script.Append("    if [ \"$cmd\" = " + parent.Name + " ]; then for ((j=i+1; j<COMP_CWORD; j++)); do case \"${COMP_WORDS[j]}\" in\n");
            script.Append("        " + string.Join('|', parent.Subcommands.Select(child => child.Name)) + ") sub=${COMP_WORDS[j]}; break ;;\n");
            script.Append("    esac; done; fi\n");
        }
        script.Append("    local opts\n");
        script.Append("    case \"$cmd\" in\n");
        script.Append("        \"\") opts='" + rootWords + "' ;;\n");
        foreach (Command command in root.Subcommands)
        {
            string ownWords = string.Join(' ', command.Options.SelectMany(FlagNames).Concat(recursive));
            if (command.Subcommands.Count == 0)
            {
                script.Append("        " + command.Name + ") opts='" + ownWords + "' ;;\n");
                continue;
            }
            string childNames = string.Join(' ', command.Subcommands.Select(child => child.Name));
            script.Append("        " + command.Name + ")\n            case \"$sub\" in\n");
            foreach (Command child in command.Subcommands)
            {
                string childWords = string.Join(' ', child.Options.SelectMany(FlagNames).Concat(recursive));
                script.Append("                " + child.Name + ") opts='" + childWords + "' ;;\n");
            }
            script.Append("                *) opts='" + childNames + " " + ownWords + "' ;;\n            esac ;;\n");
        }
        script.Append("    esac\n");
        script.Append("    COMPREPLY=( $(compgen -W \"$opts\" -- \"$cur\") )\n");
        script.Append("}\n");
        script.Append("complete -F _tmux_workspace tmux-workspace\n");
        return script.ToString();
    }

    private static string Zsh(Command root)
    {
        string[] recursive = [.. root.Options.Where(option => option.Recursive).SelectMany(FlagNames)];
        string rootWords = string.Join(' ', root.Subcommands.Select(command => command.Name).Concat(root.Options.SelectMany(FlagNames)));

        StringBuilder script = new();
        script.Append("#compdef tmux-workspace\n");
        script.Append("local cmd sub i j\n");
        script.Append("for ((i=2; i<CURRENT; i++)); do case ${words[i]} in\n");
        script.Append("    " + string.Join('|', root.Subcommands.Select(command => command.Name)) + ") cmd=${words[i]}; break ;;\n");
        script.Append("esac; done\n");
        foreach (Command parent in root.Subcommands.Where(command => command.Subcommands.Count > 0))
        {
            script.Append("if [[ $cmd == " + parent.Name + " ]]; then for ((j=i+1; j<CURRENT; j++)); do case ${words[j]} in\n");
            script.Append("    " + string.Join('|', parent.Subcommands.Select(child => child.Name)) + ") sub=${words[j]}; break ;;\n");
            script.Append("esac; done; fi\n");
        }
        script.Append("local opts\n");
        script.Append("case $cmd in\n");
        script.Append("    \"\") opts='" + rootWords + "' ;;\n");
        foreach (Command command in root.Subcommands)
        {
            string ownWords = string.Join(' ', command.Options.SelectMany(FlagNames).Concat(recursive));
            if (command.Subcommands.Count == 0)
            {
                script.Append("    " + command.Name + ") opts='" + ownWords + "' ;;\n");
                continue;
            }
            string childNames = string.Join(' ', command.Subcommands.Select(child => child.Name));
            script.Append("    " + command.Name + ")\n        case $sub in\n");
            foreach (Command child in command.Subcommands)
            {
                string childWords = string.Join(' ', child.Options.SelectMany(FlagNames).Concat(recursive));
                script.Append("            " + child.Name + ") opts='" + childWords + "' ;;\n");
            }
            script.Append("            *) opts='" + childNames + " " + ownWords + "' ;;\n        esac ;;\n");
        }
        script.Append("esac\n");
        script.Append("_arguments \"*:argument:($opts)\"\n");
        return script.ToString();
    }
}
