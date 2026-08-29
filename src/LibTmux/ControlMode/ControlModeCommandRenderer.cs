using System.Text;

namespace LibTmux;

/// <summary>Renders typed argv as one physical tmux control-input line.</summary>
internal static class ControlModeCommandRenderer
{
    internal static string Render(TmuxCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var rendered = new StringBuilder();
        foreach (string token in command.ToArguments())
        {
            if (rendered.Length > 0)
            {
                rendered.Append(' ');
            }

            AppendToken(rendered, token);
        }

        return rendered.ToString();
    }

    private static void AppendToken(StringBuilder rendered, string token)
    {
        if (token.Contains('\r', StringComparison.Ordinal)
            || token.Contains('\n', StringComparison.Ordinal))
        {
            foreach (byte value in Encoding.UTF8.GetBytes(token))
            {
                rendered.Append('\\');
                rendered.Append(Convert.ToString(value, 8).PadLeft(3, '0'));
            }

            return;
        }

        rendered.Append('\'');
        foreach (char character in token)
        {
            rendered.Append(character == '\'' ? "'\"'\"'" : character);
        }

        rendered.Append('\'');
    }
}
