using System.Globalization;

namespace LibTmux;

/// <summary>Represents a generation-independent tmux pane identifier.</summary>
public readonly record struct PaneId : IComparable<PaneId>
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

    /// <summary>Returns the canonical prefixed identifier.</summary>
    public override string ToString() => $"%{Value.ToString(CultureInfo.InvariantCulture)}";
}
