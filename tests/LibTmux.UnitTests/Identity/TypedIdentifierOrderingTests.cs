namespace LibTmux.UnitTests.Identity;

/// <summary>Holds the three typed identifiers to a usable ordering.</summary>
/// <remarks>
/// A record struct carries equality and no ordering, so sorting by one did not
/// fail to compile — it threw <see cref="InvalidOperationException" /> from
/// inside the sort, reporting only that two elements could not be compared.
/// These cases fail if the ordering is removed again.
/// </remarks>
public sealed class TypedIdentifierOrderingTests
{
    [Fact]
    public void Sorting_identifiers_orders_them_rather_than_throwing()
    {
        AssertSorts(value => new SessionId(value));
        AssertSorts(value => new WindowId(value));
        AssertSorts(value => new PaneId(value));
    }

    [Fact]
    public void Comparison_follows_the_order_tmux_hands_identifiers_out_in()
    {
        AssertCompares(value => new SessionId(value));
        AssertCompares(value => new WindowId(value));
        AssertCompares(value => new PaneId(value));
    }

    private static void AssertSorts<T>(Func<int, T> create)
        where T : IComparable<T>
    {
        T[] shuffled = [create(10), create(0), create(2)];

        // Both paths reach Comparer<T>.Default, which is what threw.
        Assert.Equal([create(0), create(2), create(10)], shuffled.OrderBy(each => each));
        Array.Sort(shuffled);
        Assert.Equal([create(0), create(2), create(10)], shuffled);
    }

    private static void AssertCompares<T>(Func<int, T> create)
        where T : IComparable<T>
    {
        T older = create(1);
        T newer = create(2);
        T same = create(1);

        Assert.True(older.CompareTo(newer) < 0);
        Assert.True(newer.CompareTo(older) > 0);
        Assert.Equal(0, older.CompareTo(same));

        // Ordering and equality must agree, or a sorted set loses an entry.
        Assert.Equal(older.Equals(same), older.CompareTo(same) == 0);
        Assert.Equal(-older.CompareTo(newer), newer.CompareTo(older));
    }

    [Fact]
    public void Relational_operators_read_the_same_way_as_CompareTo()
    {
        Assert.True(new PaneId(1) < new PaneId(2));
        Assert.True(new PaneId(1) <= new PaneId(1));
        Assert.True(new WindowId(3) > new WindowId(2));
        Assert.True(new WindowId(3) >= new WindowId(3));
        Assert.False(new SessionId(1) > new SessionId(2));
        Assert.False(new SessionId(2) < new SessionId(1));
    }
}
