using System.Globalization;

namespace LibTmux;

/// <summary>A tmux option and the .NET type its value reads as.</summary>
/// <typeparam name="T">What the value reads as.</typeparam>
/// <remarks>
/// Declared by the caller rather than catalogued: tmux has hundreds of options,
/// adds them between releases and changes a few of their types, so a closed
/// catalogue would be wrong somewhere on every release it did not track. A key
/// says how to read one option; the request methods on
/// <see cref="TmuxOptions" /> remain for anything else.
/// </remarks>
public sealed class TmuxOptionKey<T>
    where T : notnull
{
    private readonly Func<string, (bool Read, T Value)> _read;
    private readonly Func<T, string> _write;

    internal TmuxOptionKey(string name, Func<string, (bool Read, T Value)> read, Func<T, string> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _read = read;
        _write = write;
    }

    /// <summary>Gets the option's name, as tmux spells it.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string ToString() => Name;

    internal T Read(string reported) =>
        _read(reported) is (true, T value)
            ? value
            : throw new TmuxOptionException(
                $"tmux reported option {Name} as '{reported}', which is not a {typeof(T).Name}.",
                Name,
                TmuxDispatchState.Dispatched);

    internal string Write(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _write(value);
    }
}

/// <summary>Declares typed option keys, and names the ones whose type every supported tmux shares.</summary>
/// <remarks>
/// The named keys were checked against tmux's own option table from 3.2a to
/// 3.7c. <c>status</c> is not among the flags for that reason: tmux reads it as
/// a choice of <c>off</c>, <c>on</c> and a line count.
/// </remarks>
public static class TmuxOptionKey
{
    /// <summary>Gets how many lines of scrollback a pane keeps; a session option.</summary>
    public static TmuxOptionKey<int> HistoryLimit { get; } = Number("history-limit");

    /// <summary>Gets the index a session's first window gets; a session option.</summary>
    public static TmuxOptionKey<int> BaseIndex { get; } = Number("base-index");

    /// <summary>Gets how many milliseconds tmux waits after an escape for a key sequence; a server option.</summary>
    public static TmuxOptionKey<int> EscapeTime { get; } = Number("escape-time");

    /// <summary>Gets whether tmux captures the mouse; a session option.</summary>
    public static TmuxOptionKey<bool> Mouse { get; } = Flag("mouse");

    /// <summary>Gets whether a window renames itself after what its pane runs; a window option.</summary>
    public static TmuxOptionKey<bool> AutomaticRename { get; } = Flag("automatic-rename");

    /// <summary>Gets whether keys typed into one pane go to every pane in its window; a window or pane option.</summary>
    public static TmuxOptionKey<bool> SynchronizePanes { get; } = Flag("synchronize-panes");

    /// <summary>Declares an option read as the text tmux reports, which suits a string, a choice, a style or a colour.</summary>
    /// <param name="name">The option name, such as <c>status-style</c> or a user option <c>@name</c>.</param>
    /// <returns>A key reading the option as text.</returns>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    public static TmuxOptionKey<string> Text(string name) =>
        new(name, static reported => (true, reported), static value => value);

    /// <summary>Declares an option tmux holds as a whole number.</summary>
    /// <param name="name">The option name.</param>
    /// <returns>A key reading the option as a number.</returns>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    public static TmuxOptionKey<int> Number(string name) =>
        new(
            name,
            static reported => (
                int.TryParse(reported.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number),
                number),
            static value => value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Declares an option tmux holds as a flag, which it reports as <c>on</c> or <c>off</c>.</summary>
    /// <param name="name">The option name.</param>
    /// <returns>A key reading the option as a flag.</returns>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    public static TmuxOptionKey<bool> Flag(string name) =>
        new(
            name,
            static reported => reported.Trim() switch
            {
                "on" => (true, true),
                "off" => (true, false),
                _ => (false, false),
            },
            static value => value ? "on" : "off");
}
