using System.Runtime.Versioning;
using System.Text.Json;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Mcp;

[UnsupportedOSPlatform("windows")]
public sealed class WrappedRowsTests
{
    [Fact]
    public void Rows_a_line_wrapped_across_are_one_line_in_the_joined_capture()
    {
        string[] rows = ["abcdefghijklmnopqNEE", "DLE_SPLIT_here_zzzz", "next"];
        string[] joined = ["abcdefghijklmnopqNEEDLE_SPLIT_here_zzzz", "next"];

        WrappedRows pairing = Assert.IsType<WrappedRows>(WrappedRows.Pair(rows, joined));

        Assert.Equal([0, 0, 1], pairing.Group);
        Assert.Equal(["abcdefghijklmnopqNEEDLE_SPLIT_here_zzzz", null], pairing.Text);
    }

    [Fact]
    public void Spaces_a_wrapped_row_lost_to_trimming_come_back_in_the_joined_line()
    {
        string[] rows = ["foo bar", "       baz tail"];
        string[] joined = ["foo bar                    baz tail"];

        WrappedRows pairing = Assert.IsType<WrappedRows>(WrappedRows.Pair(rows, joined));

        Assert.Equal("foo bar                    baz tail", pairing.Text[0]);
    }

    [Fact]
    public void A_capture_with_nothing_wrapped_has_no_pairing() =>
        Assert.Null(WrappedRows.Pair(["one", "", "two"], ["one", "", "two"]));

    [Fact]
    public void Captures_that_disagree_have_no_pairing()
    {
        Assert.Null(WrappedRows.Pair(["abc", "def", "ghi"], ["abcXdef", "ghi"]));
        Assert.Null(WrappedRows.Pair(["abc", "def"], ["zzzdef"]));
    }

    [Fact]
    public void A_row_holding_only_spaces_inside_a_wrapped_line_belongs_to_the_line()
    {
        string[] rows = ["abcdefghijklmnopqrst", string.Empty, "END_OF_LINE"];
        string[] joined = ["abcdefghijklmnopqrst" + new string(' ', 20) + "END_OF_LINE", "next"];

        WrappedRows pairing = Assert.IsType<WrappedRows>(WrappedRows.Pair([.. rows, "next"], joined));

        Assert.Equal([0, 0, 0, 1], pairing.Group);
        Assert.Equal(joined[0], pairing.Text[0]);
    }

    [Fact]
    public void A_row_holding_only_spaces_does_not_hide_an_ordinary_wrapped_line_on_the_screen()
    {
        string[] rows =
        [
            "abcdefghijklmnopqrst", string.Empty, "END_OF_LINE",
            "abcdefghijklmnopqNEE", "DLE_SPLIT_here_zzzz",
        ];
        string[] joined =
        [
            "abcdefghijklmnopqrst" + new string(' ', 20) + "END_OF_LINE",
            "abcdefghijklmnopqNEEDLE_SPLIT_here_zzzz",
        ];

        WrappedRows pairing = Assert.IsType<WrappedRows>(WrappedRows.Pair(rows, joined));

        Assert.Equal([0, 0, 0, 1, 1], pairing.Group);
        Assert.Equal(joined[1], pairing.Text[1]);

        // The same two lines in the other order.
        WrappedRows swapped = Assert.IsType<WrappedRows>(
            WrappedRows.Pair([.. rows[3..], .. rows[..3]], [joined[1], joined[0]]));

        Assert.Equal([0, 0, 1, 1, 1], swapped.Group);
        Assert.Equal(joined[0], swapped.Text[1]);
    }

    [Fact]
    public void A_row_holding_only_spaces_between_two_lines_goes_with_the_line_after_it()
    {
        string[] rows = ["abcdefghijklmnopqrst", string.Empty, "abcdefghijklmnopqNEE", "DLE_SPLIT_here_zzzz"];
        string[] joined = ["abcdefghijklmnopqrst     ", "abcdefghijklmnopqNEEDLE_SPLIT_here_zzzz"];

        WrappedRows pairing = Assert.IsType<WrappedRows>(WrappedRows.Pair(rows, joined));

        Assert.Equal([0, 1, 1, 1], pairing.Group);
        Assert.Equal(joined[1], pairing.Text[1]);
    }

    [Fact]
    public void A_real_empty_line_is_not_taken_for_a_spill_row()
    {
        string[] rows = ["abcdef", string.Empty, "abcdefghijklmnopqNEE", "DLE_SPLIT_here_zzzz"];
        string[] joined = ["abcdef", string.Empty, "abcdefghijklmnopqNEEDLE_SPLIT_here_zzzz"];

        WrappedRows pairing = Assert.IsType<WrappedRows>(WrappedRows.Pair(rows, joined));

        Assert.Equal([0, 1, 2, 2], pairing.Group);
    }

    [Fact]
    public void A_line_the_captures_disagree_about_is_dropped_and_the_lines_around_it_are_kept()
    {
        string[] rows = ["abcdefghijklmnopqNEE", "DLE_SPLIT_here_zzzz", "abc", "def", "zzz", "ghijklmnopqrstuvwxyz", "0123456789"];
        string[] joined =
        [
            "abcdefghijklmnopqNEEDLE_SPLIT_here_zzzz",
            "abcXdef",
            "zzz",
            "ghijklmnopqrstuvwxyz0123456789",
        ];

        WrappedRows pairing = Assert.IsType<WrappedRows>(WrappedRows.Pair(rows, joined));

        Assert.Equal([0, 0, WrappedRows.Unpaired, WrappedRows.Unpaired, 2, 3, 3], pairing.Group);
        Assert.Equal(joined[0], pairing.Text[0]);
        Assert.Null(pairing.Text[1]);
        Assert.Equal(joined[3], pairing.Text[3]);
    }

    [Fact]
    public void A_line_shorter_in_the_joined_capture_leaves_the_lines_after_it_paired()
    {
        string[] rows = ["abcdef", "x", "abcdefghijklmnopqNEE", "DLE_here"];
        string[] joined = ["abc", "x", "abcdefghijklmnopqNEEDLE_here"];

        WrappedRows pairing = Assert.IsType<WrappedRows>(WrappedRows.Pair(rows, joined));

        Assert.Equal([WrappedRows.Unpaired, 1, 2, 2], pairing.Group);
        Assert.Equal(joined[2], pairing.Text[2]);
    }

    [Fact]
    public void A_pane_that_scrolled_one_line_between_the_captures_pairs_the_lines_that_remain()
    {
        string[] rows = ["first", "abcdefghijklmnopqNEE", "DLE_here", "tail"];
        string[] joined = ["abcdefghijklmnopqNEEDLE_here", "tail", "new"];

        WrappedRows pairing = Assert.IsType<WrappedRows>(WrappedRows.Pair(rows, joined));

        Assert.Equal([WrappedRows.Unpaired, 0, 0, 1], pairing.Group);
        Assert.Equal(joined[0], pairing.Text[0]);
    }

    [Fact]
    public void A_line_that_starts_with_a_pane_width_of_spaces_is_one_line()
    {
        // From tmux: printf 'x\n'; printf '%20s%s\n' '' abcdefghijklmnopqNEEDLE_ONE_here
        string[] rows = ["x", string.Empty, "abcdefghijklmnopqNEE", "DLE_ONE_here"];
        string[] joined = ["x", new string(' ', 20) + "abcdefghijklmnopqNEEDLE_ONE_here"];

        WrappedRows pairing = Assert.IsType<WrappedRows>(WrappedRows.Pair(rows, joined));

        Assert.Equal([0, 1, 1, 1], pairing.Group);
        Assert.Equal(joined[1], pairing.Text[1]);
    }

    [Fact]
    public void An_unpairable_line_followed_by_lines_that_start_with_spaces_does_not_stop_the_pairing()
    {
        // The second line is one the captures disagree about; the third starts with spaces.
        string[] rows =
        [
            "abc", "def", "   indented", "abcdefghijklmnopqNEE", "DLE_SPLIT_here_zzzz",
        ];
        string[] joined =
        [
            "abcXdef", "   indented", "abcdefghijklmnopqNEEDLE_SPLIT_here_zzzz",
        ];

        WrappedRows pairing = Assert.IsType<WrappedRows>(WrappedRows.Pair(rows, joined));

        Assert.Equal(WrappedRows.Unpaired, pairing.Group[0]);
        Assert.Equal([1, 2, 2], pairing.Group.Skip(2));
        Assert.Equal(joined[2], pairing.Text[2]);
    }

    [Fact]
    public void A_whitespace_only_line_after_a_line_indented_by_a_pane_width_leaves_the_next_line_paired()
    {
        string[] rows =
        [
            "x", string.Empty, "first", string.Empty, "abcdefghijklmnopqNEE", "DLE_TWO_here_zzzz",
        ];
        string[] joined =
        [
            "x", new string(' ', 20) + "first", "   ", "abcdefghijklmnopqNEEDLE_TWO_here_zzzz",
        ];

        WrappedRows pairing = Assert.IsType<WrappedRows>(WrappedRows.Pair(rows, joined));

        Assert.Equal([0, 1, 1, 2, 3, 3], pairing.Group);
        Assert.Equal(joined[1], pairing.Text[1]);
        Assert.Equal(joined[3], pairing.Text[3]);
    }

    [Fact]
    public void Every_pattern_on_a_real_screen_is_found_in_one_line()
    {
        using Stream input = typeof(WrappedRowsTests).Assembly
            .GetManifestResourceStream("LibTmux.Tests.WrappedCaptures.json")!;
        using JsonDocument document = JsonDocument.Parse(input);
        List<string> misses = [];
        int screens = 0;
        foreach (JsonElement screen in document.RootElement.GetProperty("screens").EnumerateArray())
        {
            screens++;
            string[] logical = [.. screen.GetProperty("lines").EnumerateArray().Select(static l => l.GetString()!)];
            string[] rows = [.. screen.GetProperty("rows").EnumerateArray().Select(static row => row.GetString()!)];
            string[] joined = [.. screen.GetProperty("joined").EnumerateArray().Select(static row => row.GetString()!)];
            PaneRead read = new(null!, rows, rows, false, false) { Wrapped = WrappedRows.Pair(rows, joined) };
            IReadOnlyList<string> matchLines = read.MatchLines();
            string name = $"{screen.GetProperty("tmux").GetString()} {screen.GetProperty("name").GetString()}";
            foreach (string matchLine in matchLines.Where(line =>
                !rows.Contains(line) && !logical.Any(whole => whole.TrimEnd(' ') == line.TrimEnd(' '))))
            {
                // A match line is a plain row or exactly one logical line, so
                // it never holds the words of two of them.
                misses.Add($"{name}: '{matchLine}' is neither a row nor one logical line");
            }

            foreach (string pattern in screen.GetProperty("patterns").EnumerateArray()
                .Select(static p => p.GetString()!)
                .Where(pattern => !matchLines.Any(line => line.Contains(pattern, StringComparison.Ordinal))))
            {
                misses.Add($"{name}: '{pattern}'");
            }
        }

        Assert.True(screens > 100, "the fixture holds the captured screens");
        Assert.True(misses.Count == 0, string.Join(Environment.NewLine, misses.Take(10)));
    }

    [Fact]
    public void A_read_that_starts_inside_a_wrapped_line_matches_that_whole_line_and_no_line_before_it()
    {
        string[] rows = ["old", "abcdefghijklmnopqNEE", "DLE_ONE_here", "new"];
        string[] joined = ["old", "abcdefghijklmnopqNEEDLE_ONE_here", "new"];
        PaneRead read = new(null!, rows[2..], rows[2..], false, false)
        {
            Wrapped = WrappedRows.Pair(rows, joined),
            CapturedIndex = [2, 3],
        };

        Assert.Equal(["abcdefghijklmnopqNEEDLE_ONE_here", "new"], read.MatchLines());
    }
}
