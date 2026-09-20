using System.Globalization;

namespace LibTmux.Internal;

internal static class TmuxLayoutSyntax
{
    private static readonly string[] LayoutNames =
    [
        "even-horizontal", "even-vertical", "main-horizontal", "main-vertical", "tiled",
        "main-horizontal-mirrored", "main-vertical-mirrored",
    ];

    internal static bool IsValidCandidate(string layout, int paneCount) =>
        layout.StartsWith('{') || (layout.Contains(',', StringComparison.Ordinal)
            ? TryCountCells(layout, out int cells) && cells >= paneCount
            : IsNamedLayout(layout, mirrored: false) || IsNamedLayout(layout, mirrored: true));

    internal static bool SupportsJsonLayout(TmuxVersion? version) =>
        version is TmuxVersion jsonVersion && jsonVersion >= TmuxVersion.Parse("3.8");

    internal static bool NeedsVersion(string layout) =>
        layout.StartsWith('{') || (!layout.Contains(',', StringComparison.Ordinal)
        && IsNamedLayout(layout, mirrored: false) != IsNamedLayout(layout, mirrored: true));

    internal static bool IsNamedLayout(string layout, bool mirrored)
    {
        if (layout.Length == 0)
        {
            return false;
        }

        ReadOnlySpan<string> names = LayoutNames.AsSpan(0, mirrored ? 7 : 5);
        int matches = 0;
        foreach (string name in names)
        {
            if (name == layout)
            {
                return true;
            }

            if (name.StartsWith(layout, StringComparison.Ordinal))
            {
                matches++;
            }
        }

        return matches == 1;
    }

    internal static string InvalidLayoutMessage(string layout, bool mirrored, string version)
    {
        string[] matches = [.. LayoutNames.Take(mirrored ? 7 : 5)
            .Where(name => name.StartsWith(layout, StringComparison.Ordinal))];
        return matches.Length > 1
            ? $"'{layout}' matches more than one layout preset: {string.Join(", ", matches)}."
            : $"{version} does not know the layout '{layout}', or its syntax is malformed.";
    }

    internal static bool TryCountCells(string layout, out int cells)
    {
        cells = 0;
        if (layout.Length < 6 || layout[4] != ','
            || !ushort.TryParse(layout.AsSpan(0, 4), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out ushort expected))
        {
            return false;
        }

        ushort checksum = 0;
        foreach (char value in layout.AsSpan(5))
        {
            if (value > 127)
            {
                return false;
            }

            checksum = unchecked((ushort)(((checksum >> 1) | ((checksum & 1) << 15)) + value));
        }

        if (checksum != expected)
        {
            return false;
        }

        var reader = new CellReader(layout.AsSpan(5));
        if (!reader.ReadCell(0) || !reader.AtEnd)
        {
            return false;
        }

        cells = reader.Cells;
        return true;
    }

    private ref struct CellReader(ReadOnlySpan<char> text)
    {
        private readonly ReadOnlySpan<char> _text = text;
        private int _offset;

        internal int Cells { get; private set; }

        internal readonly bool AtEnd => _offset == _text.Length;

        internal bool ReadCell(int depth)
        {
            if (depth > 256 || !ReadNumber() || !Consume('x') || !ReadNumber()
                || !Consume(',') || !ReadNumber() || !Consume(',') || !ReadNumber())
            {
                return false;
            }

            int separator = _offset;
            if (Consume(','))
            {
                if (!ReadNumber())
                {
                    return false;
                }

                // A following width belongs to the sibling, not a pane ID.
                if (!AtEnd && _text[_offset] == 'x')
                {
                    _offset = separator;
                }
            }

            if (AtEnd || _text[_offset] is ',' or '}' or ']')
            {
                Cells++;
                return true;
            }

            char closing = _text[_offset] switch
            {
                '{' => '}',
                '[' => ']',
                _ => '\0',
            };
            if (closing == '\0')
            {
                return false;
            }

            _offset++;
            do
            {
                if (!ReadCell(depth + 1))
                {
                    return false;
                }
            }
            while (Consume(','));

            return Consume(closing);
        }

        private bool ReadNumber()
        {
            int start = _offset;
            uint value = 0;
            while (!AtEnd && char.IsAsciiDigit(_text[_offset]))
            {
                uint digit = (uint)(_text[_offset] - '0');
                if (value > (uint.MaxValue - digit) / 10)
                {
                    return false;
                }

                value = (value * 10) + digit;
                _offset++;
            }

            return _offset > start;
        }

        private bool Consume(char value)
        {
            if (AtEnd || _text[_offset] != value)
            {
                return false;
            }

            _offset++;
            return true;
        }
    }
}
