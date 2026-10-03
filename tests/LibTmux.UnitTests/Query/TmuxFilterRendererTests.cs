using System.Linq.Expressions;
using System.Text.RegularExpressions;
using LibTmux.Query;

namespace LibTmux.UnitTests.Query;

public sealed class TmuxFilterRendererTests
{
    private static string? Render<T>(Expression<Func<T, bool>> predicate) =>
        TmuxFilterRenderer.Superset(QueryExtensions.Translate(predicate));

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
}
