namespace LibTmux.UnitTests.Snapshots;

public sealed class SnapshotTopologyValidatorTests
{
    private static readonly ServerGeneration Generation = new(91, 901);

    [Theory]
    [InlineData("duplicate-session")]
    [InlineData("missing-session")]
    [InlineData("duplicate-window-index")]
    [InlineData("session-count")]
    [InlineData("missing-placement")]
    [InlineData("duplicate-pane")]
    [InlineData("duplicate-pane-index")]
    [InlineData("physical-owner")]
    [InlineData("physical-index")]
    [InlineData("window-count")]
    [InlineData("pane-set")]
    public void Contradictory_placements_and_counts_fail(string change)
    {
        var rows = new Rows();
        switch (change)
        {
            case "duplicate-session": rows.Sessions.Add(rows.Sessions[0]); break;
            case "missing-session": rows.Windows[0]["session_id"] = "$1"; break;
            case "duplicate-window-index": rows.Windows[1]["window_index"] = "0"; break;
            case "session-count": rows.Sessions[0]["session_windows"] = "3"; break;
            case "missing-placement": rows.Panes[0]["window_index"] = "3"; break;
            case "duplicate-pane": rows.Panes.Add(rows.Panes[0]); break;
            case "duplicate-pane-index": rows.Panes.Add(new(rows.Panes[0]) { ["pane_id"] = "%1" }); break;
            case "physical-owner":
                rows.Windows[1]["window_id"] = "@1";
                rows.Panes[1]["window_id"] = "@1";
                break;
            case "physical-index": rows.Panes[1]["pane_index"] = "3"; break;
            case "window-count": rows.Windows[0]["window_panes"] = "2"; break;
            case "pane-set": rows.Panes[1]["pane_id"] = "%1"; break;
            default: throw new ArgumentException("Unknown contradiction.", nameof(change));
        }

        InconsistentSnapshotException failure = Assert.Throws<InconsistentSnapshotException>(
            () => rows.Validate(SnapshotDepth.Panes));

        Assert.Equal(SnapshotDepth.Panes, failure.RequestedDepth);
        Assert.Equal(Generation, failure.Generation);
        Assert.Equal(TmuxDispatchState.Dispatched, failure.Dispatch);
    }

    [Fact]
    public void Repeated_links_and_scalar_changes_preserve_membership()
    {
        var rows = new Rows();
        rows.Windows[0]["window_name"] = "before";
        rows.Windows[1]["window_name"] = "after";
        rows.Panes[0]["pane_active"] = "0";
        rows.Panes[1]["pane_active"] = "1";
        rows.Sessions.Add(new() { ["session_id"] = "$1", ["session_windows"] = "1" });
        rows.Windows.Add(new(rows.Windows[0]) { ["session_id"] = "$1", ["window_index"] = "8" });
        rows.Panes.Add(new(rows.Panes[0]) { ["session_id"] = "$1", ["window_index"] = "8" });

        rows.Validate(SnapshotDepth.Panes);
    }

    [Fact]
    public void Uncaptured_children_are_not_validated_as_empty()
    {
        var rows = new Rows();
        rows.Sessions[0]["session_windows"] = "not-acquired";
        SnapshotTopologyValidator.Validate(SnapshotDepth.Sessions, Generation, rows.Sessions, [], [], TestContext.Current.CancellationToken);
        rows.Sessions[0]["session_windows"] = "2";
        rows.Windows[0]["window_panes"] = "not-acquired";
        SnapshotTopologyValidator.Validate(SnapshotDepth.Windows, Generation, rows.Sessions, rows.Windows, [], TestContext.Current.CancellationToken);
        SnapshotTopologyValidator.Validate(SnapshotDepth.Panes, Generation, [], [], [], TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("bad")]
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_counts_report_a_protocol_failure(string? value)
    {
        var rows = new Rows();
        rows.Sessions[0]["session_windows"] = value;

        TmuxProtocolException failure = Assert.Throws<TmuxProtocolException>(() => rows.Validate(SnapshotDepth.Panes));
        Assert.Equal(TmuxDispatchState.Dispatched, failure.Dispatch);
    }

    private sealed class Rows
    {
        internal List<Dictionary<string, string?>> Sessions { get; } =
        [new() { ["session_id"] = "$0", ["session_windows"] = "2" }];

        internal List<Dictionary<string, string?>> Windows { get; } =
        [Window("0"), Window("5")];

        internal List<Dictionary<string, string?>> Panes { get; } =
        [Pane("0"), Pane("5")];

        internal void Validate(SnapshotDepth depth) => SnapshotTopologyValidator.Validate(
            depth, Generation, Sessions, Windows, Panes, TestContext.Current.CancellationToken);

        private static Dictionary<string, string?> Window(string index) => new()
        {
            ["session_id"] = "$0",
            ["window_id"] = "@0",
            ["window_index"] = index,
            ["window_panes"] = "1",
        };

        private static Dictionary<string, string?> Pane(string index) => new()
        {
            ["session_id"] = "$0",
            ["window_id"] = "@0",
            ["window_index"] = index,
            ["pane_id"] = "%0",
            ["pane_index"] = "2",
        };
    }
}
