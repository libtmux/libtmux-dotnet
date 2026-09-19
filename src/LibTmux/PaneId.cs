using System.Globalization;

namespace LibTmux;

/// <summary>Represents a generation-independent tmux pane identifier.</summary>
public readonly record struct PaneId
    : IComparable<PaneId>, IParsable<PaneId>, ISpanParsable<PaneId>
{
    /// <summary>Initializes a pane identifier.</summary>
    public PaneId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    /// <summary>Gets the nonnegative numeric value.</summary>
    public int Value { get; }

    /// <summary>Parses a prefixed pane identifier.</summary>
    public static PaneId Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out PaneId result)
            ? result
            : throw new FormatException("The value is not a canonical pane identifier.");
    }

    /// <summary>Tries to parse a prefixed pane identifier.</summary>
    public static bool TryParse(string? text, out PaneId result)
    {
        if (text is not null
            && text.Length > 1
            && text[0] == '%'
            && int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            result = new PaneId(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>Reports whether one identifier was handed out before another.</summary>
    /// <param name="left">The first identifier.</param>
    /// <param name="right">The second identifier.</param>
    /// <returns><see langword="true" /> when the comparison holds.</returns>
    public static bool operator <(PaneId left, PaneId right) => left.Value < right.Value;

    /// <summary>Reports whether one identifier was handed out no later than another.</summary>
    /// <param name="left">The first identifier.</param>
    /// <param name="right">The second identifier.</param>
    /// <returns><see langword="true" /> when the comparison holds.</returns>
    public static bool operator <=(PaneId left, PaneId right) => left.Value <= right.Value;

    /// <summary>Reports whether one identifier was handed out after another.</summary>
    /// <param name="left">The first identifier.</param>
    /// <param name="right">The second identifier.</param>
    /// <returns><see langword="true" /> when the comparison holds.</returns>
    public static bool operator >(PaneId left, PaneId right) => left.Value > right.Value;

    /// <summary>Reports whether one identifier was handed out no earlier than another.</summary>
    /// <param name="left">The first identifier.</param>
    /// <param name="right">The second identifier.</param>
    /// <returns><see langword="true" /> when the comparison holds.</returns>
    public static bool operator >=(PaneId left, PaneId right) => left.Value >= right.Value;

    /// <summary>Orders this identifier against another numerically.</summary>
    /// <param name="other">The identifier to compare against.</param>
    /// <returns>A negative value, zero, or a positive value.</returns>
    /// <remarks>
    /// tmux hands out pane identifiers in increasing order, so the ordering
    /// is oldest first. Without it <c>OrderBy</c> and <c>Array.Sort</c> throw
    /// rather than sorting.
    /// </remarks>
    public int CompareTo(PaneId other) => Value.CompareTo(other.Value);

    /// <summary>Parses a prefixed pane identifier from a span.</summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The parsed identifier.</returns>
    /// <exception cref="FormatException">The text is not a canonical pane identifier.</exception>
    public static PaneId Parse(ReadOnlySpan<char> text) =>
        TryParse(text, out PaneId result)
            ? result
            : throw new FormatException("The value is not a canonical pane identifier.");

    /// <summary>Tries to parse a prefixed pane identifier from a span.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="result">The parsed identifier when this succeeds.</param>
    /// <returns><see langword="true" /> when the text was a canonical identifier.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out PaneId result)
    {
        if (text.Length > 1
            && text[0] == '%'
            && int.TryParse(text[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            result = new PaneId(value);
            return true;
        }

        result = default;
        return false;
    }

    // The parse interfaces are implemented explicitly. A public overload taking
    // a format provider would make every existing Parse call read as though it
    // depended on the current culture, and none of them does: the wire form is
    // a sigil and invariant digits.
    static PaneId IParsable<PaneId>.Parse(string s, IFormatProvider? provider) => Parse(s);

    static bool IParsable<PaneId>.TryParse(string? s, IFormatProvider? provider, out PaneId result) =>
        TryParse(s, out result);

    static PaneId ISpanParsable<PaneId>.Parse(ReadOnlySpan<char> s, IFormatProvider? provider) =>
        Parse(s);

    static bool ISpanParsable<PaneId>.TryParse(
        ReadOnlySpan<char> s,
        IFormatProvider? provider,
        out PaneId result) =>
        TryParse(s, out result);

    /// <summary>Returns the canonical prefixed identifier.</summary>
    public override string ToString() => $"%{Value.ToString(CultureInfo.InvariantCulture)}";
}
