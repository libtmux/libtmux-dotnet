using System.Diagnostics.CodeAnalysis;

namespace LibTmux;

/// <summary>What tmux reported for an option.</summary>
public enum TmuxOptionState
{
    /// <summary>tmux named the option but gave it no value.</summary>
    Absent,

    /// <summary>tmux reported the flag value <c>off</c>.</summary>
    Off,

    /// <summary>tmux reported the flag value <c>on</c>.</summary>
    On,

    /// <summary>tmux reported a value that is neither <c>on</c> nor <c>off</c>.</summary>
    Value,
}

/// <summary>One option value as tmux reported it.</summary>
/// <remarks>
/// tmux has no types: every option comes back as text. The typed readings sit
/// alongside <see cref="Raw" /> rather than replacing it, so a caller that knows
/// better than the guess can always reach what tmux actually said.
/// </remarks>
public sealed record TmuxOptionValue
{
    /// <summary>Initializes an option value.</summary>
    /// <param name="raw">The unescaped text tmux reported, or null when it reported none.</param>
    /// <param name="state">Whether the value is absent, a flag, or ordinary text.</param>
    /// <param name="boolean">The flag reading, or null when the value is not a flag.</param>
    /// <param name="integer">The whole-number reading, or null when the value is not one.</param>
    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "The reviewed public surface names the whole-number reading after what tmux stores.")]
    public TmuxOptionValue(string? raw, TmuxOptionState state, bool? boolean, long? integer)
    {
        Raw = raw;
        State = state;
        Boolean = boolean;
        Integer = integer;
    }

    /// <summary>Gets the unescaped text tmux reported, or null when it reported none.</summary>
    public string? Raw { get; }

    /// <summary>Gets whether the value is absent, a flag, or ordinary text.</summary>
    public TmuxOptionState State { get; }

    /// <summary>Gets the flag reading, or null when the value is not a flag.</summary>
    public bool? Boolean { get; }

    /// <summary>Gets the whole-number reading, or null when the value is not one.</summary>
    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "The reviewed public surface names the whole-number reading after what tmux stores.")]
    public long? Integer { get; }
}

/// <summary>One option, with its array index when tmux gave it one.</summary>
/// <remarks>
/// tmux array options are sparse: setting index 40 leaves 4 through 39 unset,
/// and tmux reports only what exists. Carrying the index on each entry says so
/// plainly, where a list would have to invent the gaps.
/// </remarks>
public sealed record TmuxOption
{
    /// <summary>Initializes an option.</summary>
    /// <param name="name">The option name, without index or inheritance marker.</param>
    /// <param name="value">The value tmux reported.</param>
    /// <param name="index">The array index, or null for an option that is not an array.</param>
    /// <param name="inherited">Whether the value came from a parent scope.</param>
    public TmuxOption(string name, TmuxOptionValue value, int? index, bool inherited = false)
    {
        Name = name;
        Value = value;
        Index = index;
        Inherited = inherited;
    }

    /// <summary>Gets the option name, without index or inheritance marker.</summary>
    public string Name { get; }

    /// <summary>Gets the value tmux reported.</summary>
    public TmuxOptionValue Value { get; }

    /// <summary>Gets the array index, or null for an option that is not an array.</summary>
    public int? Index { get; }

    /// <summary>Gets whether the value came from a parent scope.</summary>
    /// <remarks>
    /// Only a read that asked for inherited values can tell. Without it tmux
    /// answers nothing at all for an option set at a wider scope, so a false
    /// value there means "not reported as inherited", not "set here".
    /// </remarks>
    public bool Inherited { get; }
}
