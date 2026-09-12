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
            "fish" => string.Join('\n', words.Select(word => "complete -c tmux-workspace -f -a '" + word + "'")) + "\n",
            _ => throw new CliException("invalid-format", "Unknown documentation format.", 2),
        };
    }
}
