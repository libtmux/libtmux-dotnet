using System.Collections;

namespace LibTmux.Query;

/// <summary>Ordered, read-only matches and the complete observation that produced them.</summary>
/// <typeparam name="T">The selected native entity type.</typeparam>
public sealed class QueryResult<T> : IReadOnlyList<T>
{
    internal QueryResult(Server snapshot, IEnumerable<T> items)
    {
        Snapshot = snapshot;
        _items = [.. items];
    }

    private readonly T[] _items;
    /// <summary>Gets the complete captured graph, including unselected entities.</summary>
    public Server Snapshot { get; }
    /// <inheritdoc />
    public int Count => _items.Length;
    /// <inheritdoc />
    public T this[int index] => _items[index];
    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
