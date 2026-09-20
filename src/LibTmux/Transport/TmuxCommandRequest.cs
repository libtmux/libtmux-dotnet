using System.Text;

namespace LibTmux.Internal;

internal sealed class TmuxCommandRequest
{
    private readonly TmuxCommandToken[] _tokens;

    private TmuxCommandRequest(TmuxCommandToken[] tokens, string[] logicalArguments, bool preventServerStart = false)
    {
        _tokens = tokens;
        LogicalArguments = logicalArguments;
        PreventServerStart = preventServerStart;
    }

    internal IReadOnlyList<string> LogicalArguments { get; }

    internal bool PreventServerStart { get; }

    internal static TmuxCommandRequest Single(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ValidateCommand(arguments, nameof(arguments));
        string[] copy = [.. arguments];
        return new TmuxCommandRequest(
            [.. copy.Select(static value => TmuxCommandToken.Argument(value))],
            copy);
    }

    internal static TmuxCommandRequest Group(params IReadOnlyList<string>[] commands) =>
        Group(preventServerStart: false, commands);

    internal static TmuxCommandRequest Group(bool preventServerStart, params IReadOnlyList<string>[] commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Length == 0)
        {
            throw new ArgumentException("At least one grouped command is required.", nameof(commands));
        }

        var tokens = new List<TmuxCommandToken>();
        var logicalArguments = new List<string>();

        for (int index = 0; index < commands.Length; index++)
        {
            IReadOnlyList<string> command = commands[index]
                ?? throw new ArgumentException("A grouped command cannot be null.", nameof(commands));
            ValidateCommand(command, nameof(commands));
            if (index > 0)
            {
                tokens.Add(TmuxCommandToken.Separator());

                // The separator belongs in the logical vector too. Without it a
                // grouped dispatch reports as one flat command, so nothing
                // reading the arguments afterwards can tell how many commands
                // tmux was actually given.
                logicalArguments.Add(";");
            }

            foreach (string argument in command)
            {
                tokens.Add(TmuxCommandToken.Argument(argument));
                logicalArguments.Add(argument);
            }
        }

        return new TmuxCommandRequest([.. tokens], [.. logicalArguments], preventServerStart);
    }

    private static void ValidateCommand(IReadOnlyList<string> command, string parameterName)
    {
        if (command.Count == 0)
        {
            throw new ArgumentException("At least one tmux argument is required.", parameterName);
        }

        if (command.Any(static argument => argument is null))
        {
            throw new ArgumentException("Tmux arguments cannot be null.", parameterName);
        }
    }

    internal IReadOnlyList<string> EncodeArguments()
    {
        var encoded = new string[_tokens.Length];
        for (int index = 0; index < _tokens.Length; index++)
        {
            TmuxCommandToken token = _tokens[index];
            encoded[index] = token.IsSeparator ? ";" : EncodeLiteral(token.Value!);
        }

        return encoded;
    }

    // MAX_IMSGSIZE minus imsg header (16) and msg_command header (4).
    internal bool FitsNativeArgumentBudget() =>
        EncodeArguments().Sum(static argument => Encoding.UTF8.GetByteCount(argument) + 1L) <= 16_364;

    private static string EncodeLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.EndsWith(';'))
        {
            return $"{value[..^1]}\\;";
        }

        return value;
    }

    private readonly record struct TmuxCommandToken(string? Value, bool IsSeparator)
    {
        internal static TmuxCommandToken Argument(string value) => new(value, IsSeparator: false);

        internal static TmuxCommandToken Separator() => new(Value: null, IsSeparator: true);
    }
}
