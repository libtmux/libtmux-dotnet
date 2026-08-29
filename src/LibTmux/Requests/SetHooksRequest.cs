using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Describes setting several entries of one hook at once.</summary>
/// <remarks>
/// A tmux hook is an array, and the indices decide the order its commands run
/// in. Writing them together is the only way to land a whole ordering without
/// the hook firing part-written in between.
/// </remarks>
public sealed record SetHooksRequest
{
    private readonly ReadOnlyDictionary<int, string> _values;

    /// <summary>Initializes a request to set several entries of one hook.</summary>
    /// <param name="name">The hook name, without an index.</param>
    /// <param name="values">The command to place at each index.</param>
    /// <param name="scope">The scope to set in, or null for the owner's own.</param>
    /// <param name="global">Whether the global table is set instead of the local one.</param>
    /// <param name="clearExisting">Whether entries already there are removed first.</param>
    public SetHooksRequest(
        string name,
        IReadOnlyDictionary<int, string> values,
        OptionScope? scope = null,
        bool global = false,
        bool clearExisting = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("A hook needs at least one entry.", nameof(values));
        }

        foreach (KeyValuePair<int, string> entry in values)
        {
            if (entry.Key < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values),
                    entry.Key,
                    "A hook index cannot be negative.");
            }

            ArgumentNullException.ThrowIfNull(entry.Value);
        }

        Name = name;

        // The request is read again at dispatch, so a caller that kept the
        // dictionary could otherwise change the argv after constructing it.
        _values = new ReadOnlyDictionary<int, string>(new Dictionary<int, string>(values));
        Scope = scope;
        Global = global;
        ClearExisting = clearExisting;
    }

    /// <summary>Gets the hook name, without an index.</summary>
    /// <remarks>
    /// tmux expands it as a format before it names anything, so a <c>#</c> in
    /// it does not survive verbatim.
    /// </remarks>
    public string Name { get; }

    /// <summary>Gets the command to place at each index.</summary>
    public IReadOnlyDictionary<int, string> Values => _values;

    /// <summary>Gets the scope to set in, or null for the owner's own.</summary>
    public OptionScope? Scope { get; }

    /// <summary>Gets whether the global table is set instead of the local one.</summary>
    public bool Global { get; }

    /// <summary>Gets whether entries already there are removed first.</summary>
    public bool ClearExisting { get; }
}
