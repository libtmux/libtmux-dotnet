using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>Describes setting several entries of one hook.</summary>
/// <remarks>
/// A tmux hook is an array, and the indices decide the order its commands run
/// in. Entries are written sequentially in index order, without a transaction.
/// The hook may fire between writes, and failure leaves earlier writes applied.
/// </remarks>
public sealed record SetHooksRequest
{
    private readonly ReadOnlyDictionary<int, string> _values;

    /// <summary>Initializes a request to set several entries of one hook.</summary>
    /// <param name="name">The hook name, without an index.</param>
    /// <param name="values">The command to place at each index.</param>
    public SetHooksRequest(
        string name,
        IReadOnlyDictionary<int, string> values)
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
    public OptionScope? Scope { get; init; }

    /// <summary>Gets whether the global table is set instead of the local one.</summary>
    public bool Global { get; init; }

    /// <summary>Gets whether entries already there are removed first.</summary>
    public bool ClearExisting { get; init; }
}
