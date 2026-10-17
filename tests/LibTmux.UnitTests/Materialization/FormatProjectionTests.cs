using LibTmux.Internal;

namespace LibTmux.UnitTests.Materialization;

public sealed class FormatProjectionTests
{
    private static readonly string[] EntityListCommands =
    [
        "list-sessions",
        "list-windows",
        "list-panes",
    ];

    [Fact]
    public void Obj_projection_covers_every_catalogued_scope()
    {
        Dictionary<string, int> counts = FormatCatalog.ObjProjection
            .SelectMany(static field => field.Scopes, static (field, scope) => scope)
            .GroupBy(static scope => scope, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);

        Assert.Equal(178, FormatCatalog.ObjProjection.Count);
        Assert.Equal(9, counts["universal"]);
        Assert.Equal(23, counts["session"]);
        Assert.Equal(34, counts["window"]);
        Assert.Equal(70, counts["pane"]);
        Assert.Equal(25, counts["client"]);
        Assert.Equal(3, counts["buffer"]);
        Assert.Equal(9, counts["event"]);
        Assert.Equal(5, counts["context"]);
    }

    [Theory]
    [InlineData("3.3a", 125)]
    [InlineData("3.6", 125)]
    [InlineData("3.7a", 136)]
    [InlineData("3.7b", 136)]
    public void Entity_list_commands_gate_fields_by_version(string version, int expected)
    {
        foreach (string listCommand in EntityListCommands)
        {
            FormatProjection projection = FormatProjection.Create(
                listCommand,
                TmuxVersion.Parse(version));

            Assert.Equal(expected, projection.Fields.Count);
            Assert.Equal(expected, projection.FramedFieldCount);
        }
    }

    [Theory]
    [InlineData("3.3a", 150)]
    [InlineData("3.6", 150)]
    [InlineData("3.7a", 161)]
    public void Client_listing_adds_client_scope_fields(string version, int expected)
    {
        FormatProjection projection = FormatProjection.Create(
            "list-clients",
            TmuxVersion.Parse(version));

        Assert.Equal(expected, projection.Fields.Count);
    }

    [Fact]
    public void Version_gates_exclude_unregistered_tokens()
    {
        FormatProjection oldest = FormatProjection.Create(
            "list-panes",
            TmuxVersion.Parse("3.3a"));
        FormatProjection newest = FormatProjection.Create(
            "list-panes",
            TmuxVersion.Parse("3.7b"));

        Assert.True(oldest.Contains("pane_dead_signal"));
        Assert.False(oldest.Contains("bracket_paste_flag"));
        Assert.True(newest.Contains("pane_dead_signal"));
        Assert.True(newest.Contains("bracket_paste_flag"));
    }

    [Fact]
    public void Context_and_event_tokens_never_reach_a_list_template()
    {
        FormatProjection projection = FormatProjection.Create(
            "list-clients",
            TmuxVersion.Parse("3.7b"));

        Assert.False(projection.Contains("search_match"));
        Assert.False(projection.Contains("mouse_x"));
        Assert.DoesNotContain("search_match", projection.Template, StringComparison.Ordinal);
    }

    [Fact]
    public void Template_expands_each_field_exactly_once()
    {
        FormatProjection projection = FormatProjection.Create(
            "list-sessions",
            TmuxVersion.Parse("3.7b"));

        // A byte-count prefix would expand every field a second time, and a
        // field that moved in between would announce one length and then
        // render another, desynchronising the rest of the row.
        Assert.DoesNotContain("#{n:", projection.Template, StringComparison.Ordinal);
        Assert.All(
            projection.Fields,
            field =>
            {
                string once = $"#{{{field.WireName}}}";
                Assert.Contains(
                    $"{once}{FormatProjection.RowSeparator}",
                    projection.Template,
                    StringComparison.Ordinal);
                Assert.Equal(
                    1,
                    projection.Template.Split(once, StringSplitOptions.None).Length - 1);
            });
    }

    [Fact]
    public void Template_stays_clear_of_the_tmux_command_ceiling()
    {
        // tmux caps a whole command at MAX_IMSGSIZE (16 KiB), and the template
        // shares that budget with the generation guard wrapped around every
        // entity command. list-clients projects the most fields.
        foreach (string listCommand in EntityListCommands.Append("list-clients"))
        {
            FormatProjection projection = FormatProjection.Create(
                listCommand,
                TmuxVersion.Parse("3.7b"));

            Assert.True(
                projection.Template.Length < 8192,
                $"{listCommand} template is {projection.Template.Length} bytes");
        }
    }
}
