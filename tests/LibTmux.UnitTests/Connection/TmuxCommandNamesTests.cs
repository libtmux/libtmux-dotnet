using LibTmux.Internal;

namespace LibTmux.UnitTests.Connection;

public sealed class TmuxCommandNamesTests
{
    [Theory]
    [InlineData("3.3a", "ru", "run-shell")]
    [InlineData("3.3a", "run", "run-shell")]
    [InlineData("3.3a", "run-s", "run-shell")]
    [InlineData("3.3a", "br", "break-pane")]
    [InlineData("3.3a", "breakp", "break-pane")]
    [InlineData("3.3a", "break-p", "break-pane")]
    [InlineData("3.3a", "new", "new-session")]
    [InlineData("3.3a", "neww", "new-window")]
    [InlineData("3.3a", "new-w", "new-window")]
    [InlineData("3.3a", "del", "delete-buffer")]
    [InlineData("3.3a", "set-b", "set-buffer")]
    [InlineData("3.3a", "sp", "split-window")]
    [InlineData("3.3a", "select-l", "select-layout")]
    [InlineData("3.7", "new-p", "new-pane")]
    [InlineData("3.7", "swi", "switch-client")]
    [InlineData("3.3a", "new-p", null)]
    [InlineData("3.3a", "swi", "switch-client")]
    [InlineData("3.8", "swi", null)]
    [InlineData("3.8", "switch-m", "switch-mode")]
    [InlineData("3.3a", "set", "set-option")]
    [InlineData("3.3a", "show", "show-options")]
    [InlineData("3.8", "set", "set-option")]
    [InlineData("3.8", "show", "show-options")]
    [InlineData("3.3a", "se", null)]
    [InlineData("3.3a", "sh", null)]
    [InlineData("3.3a", "r", null)]
    [InlineData("3.3a", "", null)]
    [InlineData("3.3a", "not-a-command", null)]
    public void A_word_resolves_the_way_tmux_resolves_it(string release, string word, string? expected) =>
        Assert.Equal(expected, TmuxCommandNames.Resolve(TmuxVersion.Parse(release), word));

    [Fact]
    public void A_command_added_in_a_later_release_is_absent_before_it()
    {
        Assert.DoesNotContain(TmuxCommandNames.Listed(TmuxVersion.Parse("3.6b")), entry => entry.Name == "new-pane");
        Assert.Contains(TmuxCommandNames.Listed(TmuxVersion.Parse("3.7")), entry => entry.Name == "new-pane");
        Assert.DoesNotContain(TmuxCommandNames.Listed(TmuxVersion.Parse("3.7c")), entry => entry.Name == "switch-mode");
        Assert.Contains(TmuxCommandNames.Listed(TmuxVersion.Parse("3.8")), entry => entry.Name == "switch-mode");
    }
}
