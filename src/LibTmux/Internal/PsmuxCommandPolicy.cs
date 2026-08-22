using System.Globalization;

namespace LibTmux.Internal;

internal static class PsmuxCommandPolicy
{
    internal static void Validate(IReadOnlyList<string> arguments)
    {
        foreach (string argument in arguments)
        {
            ValidateArgument(argument);
        }

        int targetCount = 0;
        int targetOperand = -1;
        bool afterEndOfOptions = false;
        for (int index = 1; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--", StringComparison.Ordinal))
            {
                afterEndOfOptions = true;
                continue;
            }

            if (!string.Equals(arguments[index], "-t", StringComparison.Ordinal))
            {
                continue;
            }

            targetCount++;
            if (targetCount > 1 || afterEndOfOptions)
            {
                throw new NotSupportedException(
                    "psmux cannot distinguish an additional -t token from command payload.");
            }

            if (index + 1 >= arguments.Count)
            {
                throw new NotSupportedException("psmux target options require an operand.");
            }

            targetOperand = index + 1;
        }

        if (targetOperand >= 0)
        {
            PsmuxTargetGrammar.ValidateTarget(arguments[targetOperand]);
        }

        if (!IsSupportedReadCommand(arguments))
        {
            throw new NotSupportedException(
                "The psmux 3.3.8 preview supports read and query commands only.");
        }
    }

    internal static bool CanRunWithoutSession(string command) =>
        command is "list-sessions" or "has-session";

    internal static void ValidateArgument(string argument)
    {
        if (argument.Length == 0)
        {
            throw new NotSupportedException(
                "psmux 3.3.8 cannot preserve empty command arguments.");
        }

        if (argument.Contains('\0') || argument.Contains('\r') || argument.Contains('\n'))
        {
            throw new NotSupportedException(
                "psmux 3.3.8 commands cannot safely contain NUL, CR, or LF characters.");
        }

        if (argument.Contains('\'')
            || argument.Contains('"')
            || argument.Contains("\\\\", StringComparison.Ordinal)
            || argument.EndsWith('\\'))
        {
            throw new NotSupportedException(
                "psmux 3.3.8 cannot preserve quotes, consecutive backslashes, or a trailing backslash in command arguments.");
        }

        if (argument.Contains(';'))
        {
            throw new NotSupportedException("psmux commands cannot safely contain semicolons.");
        }

        if (argument.Contains("#(", StringComparison.Ordinal)
            || argument.Contains("#{E", StringComparison.Ordinal)
            || argument.Contains("#{T", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "psmux preview commands cannot contain shell-command format expansion.");
        }
    }

    private static bool IsSupportedReadCommand(IReadOnlyList<string> arguments)
    {
        string command = arguments[0];
        return command switch
        {
            "has-session" => IsSupportedHasSession(arguments),
            "list-sessions" => IsSupportedListCommand(
                arguments,
                allowAll: false,
                allowSessionScope: false,
                allowTarget: false),
            "list-windows" => IsSupportedListCommand(
                arguments,
                allowAll: true,
                allowSessionScope: false,
                allowTarget: true),
            "list-panes" => IsSupportedListCommand(
                arguments,
                allowAll: true,
                allowSessionScope: true,
                allowTarget: true),
            "display-message" => IsSupportedDisplayMessage(arguments),
            "capture-pane" => IsSupportedCapturePane(arguments),
            _ => false,
        };
    }

    private static bool IsSupportedHasSession(IReadOnlyList<string> arguments) =>
        arguments.Count == 1
        || (arguments.Count == 3 && arguments[1] == "-t");

    private static bool IsSupportedListCommand(
        IReadOnlyList<string> arguments,
        bool allowAll,
        bool allowSessionScope,
        bool allowTarget)
    {
        bool hasAll = false;
        bool hasSessionScope = false;
        bool hasTarget = false;
        bool hasFormat = false;
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "-a" && allowAll && !hasAll)
            {
                hasAll = true;
                continue;
            }

            if (argument == "-s" && allowSessionScope && !hasSessionScope)
            {
                hasSessionScope = true;
                continue;
            }

            if (argument == "-t" && allowTarget && !hasTarget)
            {
                hasTarget = true;
                if (++index >= arguments.Count)
                {
                    return false;
                }

                continue;
            }

            if (argument == "-F" && !hasFormat)
            {
                hasFormat = true;
                if (++index >= arguments.Count)
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsSupportedDisplayMessage(IReadOnlyList<string> arguments)
    {
        bool prints = false;
        bool hasTarget = false;
        bool hasDuration = false;
        bool hasMessage = false;
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "-p" && !prints)
            {
                prints = true;
                continue;
            }

            if (argument == "-t" && !hasTarget)
            {
                hasTarget = true;
                if (++index >= arguments.Count)
                {
                    return false;
                }

                continue;
            }

            if (argument == "-d" && !hasDuration)
            {
                hasDuration = true;
                if (++index >= arguments.Count
                    || !IsCanonicalInteger(arguments[index], allowDash: false))
                {
                    return false;
                }

                continue;
            }

            if (argument.StartsWith('-') || hasMessage)
            {
                return false;
            }

            hasMessage = true;
        }

        return prints;
    }

    private static bool IsSupportedCapturePane(IReadOnlyList<string> arguments)
    {
        bool prints = false;
        bool escapes = false;
        bool joins = false;
        bool hasTarget = false;
        bool hasStart = false;
        bool hasEnd = false;
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "-p" && !prints)
            {
                prints = true;
                continue;
            }

            if (argument == "-e" && !escapes)
            {
                escapes = true;
                continue;
            }

            if (argument == "-J" && !joins)
            {
                joins = true;
                continue;
            }

            if (argument == "-t" && !hasTarget)
            {
                hasTarget = true;
                if (++index >= arguments.Count)
                {
                    return false;
                }

                continue;
            }

            if (argument == "-S" && !hasStart)
            {
                hasStart = true;
                if (++index >= arguments.Count
                    || !IsCanonicalInteger(arguments[index], allowDash: true))
                {
                    return false;
                }

                continue;
            }

            if (argument == "-E" && !hasEnd)
            {
                hasEnd = true;
                if (++index >= arguments.Count
                    || !IsCanonicalInteger(arguments[index], allowDash: true))
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        return prints;
    }

    private static bool IsCanonicalInteger(string value, bool allowDash)
    {
        if (allowDash && value == "-")
        {
            return true;
        }

        return int.TryParse(
                value,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out int parsed)
            && string.Equals(
                value,
                parsed.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

}
