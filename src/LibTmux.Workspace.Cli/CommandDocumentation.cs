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
        IEnumerable<Command> Commands(Command command) => new[] { command }.Concat(command.Subcommands.SelectMany(Commands));
        string[] words = Commands(graph.Root).SelectMany(command => command.Subcommands.Select(child => child.Name).Concat(command.Options.SelectMany(option => new[] { option.Name }.Concat(option.Aliases)))).Distinct(StringComparer.Ordinal).ToArray();
        string vocabulary = string.Join(' ', words);
        return format switch
        {
            "bash" => "_tmux_workspace() { COMPREPLY=( $(compgen -W '" + vocabulary + "' -- \"${COMP_WORDS[COMP_CWORD]}\") ); }\ncomplete -F _tmux_workspace tmux-workspace\n",
            "zsh" => "#compdef tmux-workspace\n_arguments '*:argument:(" + vocabulary + ")'\n",
            "fish" => Fish(graph.Root),
            _ => throw new CliException("invalid_format", "Unknown documentation format.", 2),
        };
    }

    // `load --<Tab>` in fish offered every subcommand's flags, not just
    // load's own 11 -- scope each `complete` line to the subcommand path it
    // belongs to, the way fish's own completions do.
    private static string Fish(Command root)
    {
        static string LongOrShort(string flag) => flag.StartsWith("--", StringComparison.Ordinal)
            ? "-l " + flag[2..]
            : "-s " + flag[1..];
        static string Quoted(string text) => "'" + text.Replace("'", "\\'", StringComparison.Ordinal) + "'";

        StringBuilder result = new();
        void EmitOption(Option option, string condition)
        {
            string flags = string.Join(' ', new[] { option.Name }.Concat(option.Aliases).Select(LongOrShort).Distinct(StringComparer.Ordinal));
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
}
