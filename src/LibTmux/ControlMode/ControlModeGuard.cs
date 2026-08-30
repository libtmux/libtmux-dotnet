using System.Globalization;

namespace LibTmux;

internal enum ControlModeGuardKind
{
    Begin,
    End,
    Error,
}

/// <summary>One parsed tmux control-mode block guard.</summary>
internal readonly record struct ControlModeGuard(
    ControlModeGuardKind Kind,
    long Timestamp,
    long Number,
    int Flags)
{
    internal bool Matches(ControlModeGuard begin) =>
        Timestamp == begin.Timestamp
        && Number == begin.Number
        && Flags == begin.Flags;

    internal static bool HasReservedName(string line) =>
        HasName(line, "%begin")
        || HasName(line, "%end")
        || HasName(line, "%error");

    internal static bool TryParse(string line, out ControlModeGuard guard)
    {
        string[] fields = line.Split(' ');
        ControlModeGuardKind? kind = fields.Length == 4
            ? fields[0] switch
            {
                "%begin" => ControlModeGuardKind.Begin,
                "%end" => ControlModeGuardKind.End,
                "%error" => ControlModeGuardKind.Error,
                _ => null,
            }
            : null;
        if (kind is null
            || !long.TryParse(
                fields[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long timestamp)
            || !long.TryParse(
                fields[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long number)
            || !int.TryParse(
                fields[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int flags)
            || flags is not (0 or 1))
        {
            guard = default;
            return false;
        }

        guard = new ControlModeGuard(kind.Value, timestamp, number, flags);
        return true;
    }

    private static bool HasName(string line, string name) =>
        line.StartsWith(name, StringComparison.Ordinal)
        && (line.Length == name.Length || char.IsWhiteSpace(line[name.Length]));
}
