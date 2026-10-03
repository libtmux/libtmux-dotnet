using System.Linq.Expressions;
using System.Text.RegularExpressions;
using LibTmux.Query;

namespace LibTmux.UnitTests.Query;

public sealed class TmuxFilterRendererTests
{
    private static string? Render<T>(Expression<Func<T, bool>> predicate) =>
        TmuxFilterRenderer.Superset(QueryExtensions.Translate(predicate));

    [Fact]
    public void Identifier_scopes_nest_evenly_and_give_way_to_the_filter_when_long()
    {
        Assert.Equal("#{==:#{session_id},$1}", TmuxFilterRenderer.AnyOf("session_id", ["$1"]));
        Assert.Equal(
            "#{||:#{==:#{session_id},$1},#{||:#{==:#{session_id},$2},#{==:#{session_id},$3}}}",
            TmuxFilterRenderer.AnyOf("session_id", ["$1", "$2", "$3"]));
        Assert.Equal(
            "#{||:#{||:#{==:#{pane_id},%1},#{==:#{pane_id},%2}},#{||:#{==:#{pane_id},%3},#{==:#{pane_id},%4}}}",
            TmuxFilterRenderer.AnyOf("pane_id", ["%1", "%2", "%3", "%4"]));

        string[] most = [.. Enumerable.Range(0, TmuxFilterRenderer.MostScopedIdentifiers).Select(id => $"${id}")];
        Assert.StartsWith("#{||:", TmuxFilterRenderer.ScopeTo("session_id", most, "found"), StringComparison.Ordinal);
        Assert.Equal("found", TmuxFilterRenderer.ScopeTo("session_id", [.. most, "$999999"], "found"));
    }

    [Fact]
    public void Operands_escape_format_and_glob_metacharacters()
    {
        Assert.Equal("#{==:#{session_name},a#,b#}c##d(}", Render<Session>(s => s.Name == "a,b}c#d("));
        Assert.Equal(@"#{m:x\*y\?\[z\\*,#{window_name}}", Render<Window>(w => w.Name.StartsWith(@"x*y?[z\", StringComparison.Ordinal)));
        Assert.Equal("#{m:*##(id)*,#{window_name}}", Render<Window>(w => w.Name.Contains("#(id)", StringComparison.Ordinal)));
        Assert.Equal("#{m:*end,#{pane_current_command}}", Render<Pane>(p => p.CurrentCommand!.EndsWith("end", StringComparison.Ordinal)));
    }

    [Fact]
    public void Text_without_a_portable_spelling_is_left_to_the_recheck()
    {
        Assert.Null(Render<Session>(s => s.Name == "#[fg=red]"));

        // Glob escaping turns the bracket into an escape, which has a spelling.
        Assert.Equal(@"#{m:##\[x*,#{session_name}}", Render<Session>(s => s.Name.StartsWith("#[x", StringComparison.Ordinal)));
    }

    [Fact]
    public void Case_insensitive_regex_and_null_operations_stay_local()
    {
        Assert.Null(Render<Session>(s => s.Name.Equals("dev", StringComparison.OrdinalIgnoreCase)));
        Assert.Null(Render<Session>(s => s.Name.Contains("dev", StringComparison.OrdinalIgnoreCase)));
        Assert.Null(Render<Session>(s => Regex.IsMatch(s.Name, "^dev", RegexOptions.CultureInvariant)));
        Assert.Null(Render<Pane>(p => p.CurrentCommand == null));
    }

    [Fact]
    public void Negation_swaps_bounds_so_unknown_operands_widen_rather_than_drop()
    {
        const string dev = "#{==:#{session_name},dev}";
        Assert.Equal(dev, Render<Session>(s => s.Name == "dev" && s.Name.Contains("XY", StringComparison.OrdinalIgnoreCase)));
        Assert.Null(Render<Session>(s => s.Name == "dev" || s.Name.Contains("XY", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal($"#{{?{dev},0,1}}", Render<Session>(s => s.Name != "dev"));
        Assert.Null(Render<Session>(s => !(s.Name == "dev" && s.Name.Contains("XY", StringComparison.OrdinalIgnoreCase))));
        Assert.Equal($"#{{?{dev},0,1}}", Render<Session>(s => !(s.Name == "dev" || s.Name.Contains("XY", StringComparison.OrdinalIgnoreCase))));
    }

    [Fact]
    public void Conjunctions_nest_as_balanced_pairs()
    {
        Assert.Equal(
            "#{&&:#{==:#{window_name},a},#{&&:#{==:#{window_name},b},#{==:#{window_name},c}}}",
            Render<Window>(w => w.Name == "a" && w.Name == "b" && w.Name == "c"));
        Assert.Equal(
            "#{||:#{||:#{==:#{window_name},a},#{==:#{window_name},b}},#{||:#{==:#{window_name},c},#{==:#{window_name},d}}}",
            Render<Window>(w => w.Name == "a" || w.Name == "b" || w.Name == "c" || w.Name == "d"));
    }

    [Fact]
    public void Flags_identifiers_and_counts_use_tmux_comparisons()
    {
        var window = new WindowId(4);
        Assert.Equal("#{session_attached}", Render<Session>(s => s.Attached));
        Assert.Equal("#{?#{session_attached},0,1}", Render<Session>(s => !s.Attached));
        Assert.Equal("#{==:#{client_control_mode},1}", Render<Client>(c => c.IsControlClient));
        Assert.Equal("#{==:#{window_id},@4}", Render<Window>(w => w.Id == window));
        Assert.Equal("#{?#{==:#{window_id},@4},0,1}", Render<Window>(w => w.Id != window));
    }

    [Fact]
    public void An_absent_number_matches_only_inequality_though_tmux_reads_it_as_0()
    {
        Assert.Equal(
            "#{&&:#{!=:#{pane_dead_status},},#{e|==|:#{pane_dead_status},0}}",
            Render<Pane>(p => p.DeadStatus == 0));
        Assert.Equal(
            "#{||:#{==:#{pane_dead_status},},#{e|!=|:#{pane_dead_status},0}}",
            Render<Pane>(p => p.DeadStatus != 0));
        Assert.Equal("#{e|<|:#{pane_width},10}", Render<Pane>(p => p.Width < 10));
    }

    [Fact]
    public void Relations_loop_over_the_rows_own_children()
    {
        Assert.Equal(
            "#{W:#{?#{==:#{window_name},edit},1,}}",
            Render<Session>(s => s.Windows.Any(w => w.Name == "edit")));
        Assert.Equal(
            "#{?#{P:#{?#{==:#{pane_current_command},sh},,1}},0,1}",
            Render<Window>(w => w.Panes.All(p => p.CurrentCommand == "sh")));
        Assert.Equal(
            "#{W:#{?#{P:#{?#{m:vi*,#{pane_current_command}},1,}},1,}}",
            Render<Session>(s => s.Windows.Any(w => w.Panes.Any(p => p.CurrentCommand!.StartsWith("vi", StringComparison.Ordinal)))));
    }

    [Fact]
    public void Empty_conjunctions_and_disjunctions_render_as_their_identities()
    {
        static QueryDocument Of(QueryNode node) =>
            new(QueryDocument.CurrentSchema, QueryDocument.CurrentVersion, QueryTarget.Session, node);

        Assert.Null(TmuxFilterRenderer.Superset(Of(new AndNode([]))));
        Assert.Equal("0", TmuxFilterRenderer.Superset(Of(new OrNode([]))));
        Assert.Null(TmuxFilterRenderer.Superset(Of(new NotNode(new OrNode([])))));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("#{pane_active}")]
    [InlineData("#{&&:#{pane_active},#{==:#{pane_index},0}}")]
    [InlineData("x#,y#}")]
    public void A_single_raw_expression_may_be_combined(string filter) =>
        TmuxFilterRenderer.RequireSingleExpression(filter);

    [Theory]
    [InlineData("1,x")]
    [InlineData("#{pane_active")]
    [InlineData("a}")]
    [InlineData("#{==:a,b},1")]
    public void A_raw_filter_that_would_split_a_combination_is_refused(string filter) =>
        Assert.Throws<ArgumentException>(() => TmuxFilterRenderer.RequireSingleExpression(filter));
}
