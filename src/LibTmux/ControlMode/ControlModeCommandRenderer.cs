using System.Text;

namespace LibTmux;

/// <summary>Renders typed argv as one physical tmux control-input line.</summary>
internal static class ControlModeCommandRenderer
{
    internal static string Render(TmuxCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var rendered = new StringBuilder();
        AppendToken(rendered, command.Name);
        foreach (string token in command.Arguments)
        {
            rendered.Append(' ');
            AppendToken(rendered, token);
        }

        return rendered.ToString();
    }

    internal static long GetRenderedByteCount(TmuxCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        long bytes = GetTokenByteCount(command.Name);
        foreach (string token in command.Arguments)
        {
            bytes += 1 + GetTokenByteCount(token);
        }

        return bytes;
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

    private static long GetTokenByteCount(string token)
    {
        int utf8Bytes = Encoding.UTF8.GetByteCount(token);
        if (token.Contains('\r', StringComparison.Ordinal)
            || token.Contains('\n', StringComparison.Ordinal))
        {
            return (long)utf8Bytes * 4;
        }

        long quotedBytes = utf8Bytes + 2L;
        foreach (char character in token)
        {
            if (character == '\'')
            {
                quotedBytes += 4;
            }
        }

        return quotedBytes;
    }
}
