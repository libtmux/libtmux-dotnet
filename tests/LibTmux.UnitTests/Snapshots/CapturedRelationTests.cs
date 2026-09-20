using System.Diagnostics;
using System.Runtime.Versioning;
using LibTmux.Internal;
using LibTmux.Query;
using LibTmux.UnitTests.Transport;

namespace LibTmux.UnitTests.Snapshots;

public sealed class CapturedRelationTests
{
    [Fact(Skip = "Requires a Unix process environment.", SkipType = typeof(UnixTestEnvironment), SkipUnless = nameof(UnixTestEnvironment.IsUnix))]
    [UnsupportedOSPlatform("windows")]
    public async Task Concurrent_first_reads_of_Windows_all_see_the_capture()
    {
        var dispatcher = new TmuxCommandDispatcher(
            static (_, _) => throw new UnreachableException());
        Window[] windows = [new(dispatcher, "@1")];
        CapturedRelation<Window> captured = CapturedRelation.Capture(windows, "windows", SnapshotDepth.Windows);
        Session session = new Session(dispatcher, "$1").WithCaptured(
            captured,
            CapturedRelation.Capture<Pane>([], "panes", SnapshotDepth.Panes));

        CapturedRelation<Window>[] both = await Task.WhenAll(
            Task.Run(() => session.Windows),
            Task.Run(() => session.Windows));

        Assert.All(both, relation => Assert.Same(captured, relation));
        Assert.Same(windows[0], Assert.Single(session.Windows));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Pane_text_fields_distinguish_uncaptured_null_and_empty_values()
    {
        var generation = new ServerGeneration(92, 902);
        var connection = new TmuxConnection(
            new ServerConnectionOptions
            {
                SocketName = "snapshot-unit"
            },
            (_, _) => throw new InvalidOperationException("A captured field reached tmux."));
        var server = new Server(connection, generation, "tmux 3.7");
        var uncaptured = new Pane(new TmuxCommandDispatcher(static (_, _) => throw new UnreachableException()), "%0");
        var missing = new Pane(server, connection, generation, new PaneId(0),
            new Dictionary<string, string?>());
        var unavailable = new Pane(server, connection, generation, new PaneId(0),
            new Dictionary<string, string?>
            {
                ["pane_current_command"] = null,
                ["pane_current_path"] = null,
            });
        var empty = new Pane(server, connection, generation, new PaneId(0),
            new Dictionary<string, string?>
            {
                ["pane_current_command"] = string.Empty,
                ["pane_current_path"] = string.Empty,
            });
        Func<Pane, bool> isNull = QueryExtensions.Translate<Pane>(pane => pane.CurrentCommand == null).Compile<Pane>();

        foreach (Pane pane in new[] { uncaptured, missing })
        {
            Assert.Throws<IncompleteSnapshotException>(() => pane.CurrentCommand);
            Assert.Throws<IncompleteSnapshotException>(() => pane.CurrentPath);
            Assert.Throws<IncompleteSnapshotException>(() => isNull(pane));
        }

        Assert.Null(unavailable.CurrentCommand);
        Assert.Null(unavailable.CurrentPath);
        Assert.True(isNull(unavailable));
        Assert.Equal(string.Empty, empty.CurrentCommand);
        Assert.Equal(string.Empty, empty.CurrentPath);
        Assert.False(isNull(empty));
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
    public void OrEmpty_does_not_expose_writable_captured_children()
    {
        CapturedRelation<int> captured =
            CapturedRelation.Capture([7], "panes", SnapshotDepth.Panes);
        IReadOnlyList<int> children = captured.OrEmpty();

        if (children is IList<int> writable)
        {
            Assert.Throws<NotSupportedException>(() => writable[0] = 99);
        }

        Assert.Equal(7, captured[0]);
        Assert.Equal(7, children[0]);
    }

    [Fact]
    public void An_empty_capture_is_distinct_from_no_capture()
    {
        CapturedRelation<int> empty =
            CapturedRelation.Capture<int>([], "windows", SnapshotDepth.Windows);

        Assert.True(empty.IsCaptured);
        Assert.Empty(empty);
        Assert.Empty(empty.OrEmpty());
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
    public void Window_edges_key_each_placement_in_the_session()
    {
        var edge = new SessionWindowEdge
        {
            SessionId = SessionId.Parse("$1"),
            WindowId = WindowId.Parse("@2"),
            WindowIndex = 3,
        };

        Assert.Null(edge.Ordinal);
        Assert.Equal(new WindowEntityKey(SessionId.Parse("$1"), WindowId.Parse("@2"), 3), edge.Key);
        Assert.NotEqual(edge.Key, (edge with { WindowIndex = 5 }).Key);
        Assert.Equal(edge.Key, (edge with { Ordinal = 5 }).Key);
        Assert.Equal(5, (edge with { Ordinal = 5 }).Ordinal);
        Assert.Equal("$1:3:@2", edge.Key.ToString());
    }
}
