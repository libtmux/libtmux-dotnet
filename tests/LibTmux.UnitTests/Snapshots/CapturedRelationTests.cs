using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.Internal;
using LibTmux.UnitTests.Transport;

namespace LibTmux.UnitTests.Snapshots;

public sealed class CapturedRelationTests
{
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Concurrent_first_reads_of_Windows_all_see_the_capture()
    {
        // The first read caches the copy and releases the factory. A second
        // reader that arrives between those two stores must not conclude the
        // windows were never captured and cache that over the real answer.
        for (int attempt = 0; attempt < 200; attempt++)
        {
            var dispatcher = new TmuxCommandDispatcher(
                static (_, _) => throw new UnreachableException());
            Window[] windows = [new(dispatcher, "@1")];
            Session session = new Session(dispatcher, "$1").WithCaptured(
                () => CapturedRelation.Capture(windows, "windows", SnapshotDepth.Windows),
                CapturedRelation.Capture<Pane>([], "panes", SnapshotDepth.Panes));

            using var gate = new Barrier(2);
            Task<CapturedRelation<Window>> Read() => Task.Run(() =>
            {
                gate.SignalAndWait();
                return session.Windows;
            });

            CapturedRelation<Window>[] both = await Task.WhenAll(Read(), Read());

            Assert.All(both, relation => Assert.True(relation.IsCaptured));
            Assert.True(session.Windows.IsCaptured);
        }
    }

    [Fact]
    public void Captured_relations_expose_their_children()
    {
        CapturedRelation<int> captured =
            CapturedRelation.Capture([1, 2, 3], "windows", SnapshotDepth.Windows);

        Assert.True(captured.IsCaptured);
        Assert.Equal(3, captured.Count);
        Assert.Equal(2, captured[1]);
        Assert.Equal([1, 2, 3], captured);
    }

    [Fact]
    public void An_uncaptured_single_value_refuses_to_look_absent()
    {
        CapturedValue<string> uncaptured =
            CapturedValue.Uncaptured<string>("active window", SnapshotDepth.Sessions);

        Assert.False(uncaptured.IsCaptured);
        Assert.Null(uncaptured.OrNull());
        Assert.False(uncaptured.TryGetValue(out string? absent));
        Assert.Null(absent);

        // "nobody looked" is a LibTmux failure carrying the relation, not the
        // InvalidOperationException a Single() over a plural relation threw.
        IncompleteSnapshotException error =
            Assert.Throws<IncompleteSnapshotException>(() => uncaptured.Value);
        Assert.Contains("active window", error.Message, StringComparison.Ordinal);

        CapturedValue<string> captured =
            CapturedValue.Capture("window", "active window", SnapshotDepth.Windows);
        Assert.True(captured.TryGetValue(out string? read));
        Assert.Equal("window", read);
        Assert.Equal("window", captured.Value);
    }

    [Fact]
    public void Uncaptured_relations_refuse_to_look_empty()
    {
        CapturedRelation<int> uncaptured =
            CapturedRelation.Uncaptured<int>("panes", SnapshotDepth.Sessions);

        Assert.False(uncaptured.IsCaptured);
        // Reading an unread relation must not report zero children.
        IncompleteSnapshotException error =
            Assert.Throws<IncompleteSnapshotException>(() => uncaptured.Count);
        Assert.Equal("panes", error.Relation);
        Assert.Equal(SnapshotDepth.Sessions, error.CapturedDepth);
        Assert.Throws<IncompleteSnapshotException>(() => uncaptured[0]);
        Assert.Throws<IncompleteSnapshotException>(() => uncaptured.ToList());
    }

    [Fact]
    public void OrEmpty_opts_into_a_lenient_read()
    {
        CapturedRelation<int> uncaptured =
            CapturedRelation.Uncaptured<int>("panes", SnapshotDepth.Sessions);

        Assert.Empty(uncaptured.OrEmpty());
        Assert.Equal(
            [7],
            CapturedRelation.Capture([7], "panes", SnapshotDepth.Panes).OrEmpty());
    }

    [Fact]
    public void An_empty_capture_is_distinct_from_no_capture()
    {
        CapturedRelation<int> empty =
            CapturedRelation.Capture<int>([], "windows", SnapshotDepth.Windows);

        Assert.True(empty.IsCaptured);
        Assert.Empty(empty);
    }

    [Fact]
    public void Capture_rejects_a_missing_sequence_or_relation()
    {
        Assert.Throws<ArgumentNullException>(
            () => CapturedRelation.Capture<int>(null!, "windows", SnapshotDepth.Windows));
        Assert.Throws<ArgumentException>(
            () => CapturedRelation.Capture<int>([], " ", SnapshotDepth.Windows));
        Assert.Throws<ArgumentException>(
            () => CapturedRelation.Uncaptured<int>("", SnapshotDepth.Windows));
    }

    [Fact]
    public void Window_edges_key_a_window_by_the_session_that_links_it()
    {
        var edge = new SessionWindowEdge
        {
            SessionId = SessionId.Parse("$1"),
            WindowId = WindowId.Parse("@2"),
            WindowIndex = 3,
        };

        Assert.Null(edge.Ordinal);
        Assert.Equal(new WindowEntityKey(SessionId.Parse("$1"), WindowId.Parse("@2")), edge.Key);
        Assert.Equal(5, (edge with { Ordinal = 5 }).Ordinal);
        Assert.Equal("$1:@2", edge.Key.ToString());
    }
}
