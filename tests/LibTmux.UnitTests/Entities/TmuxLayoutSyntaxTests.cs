using System.Globalization;
using System.Text.Json;
using LibTmux.Internal;

namespace LibTmux.UnitTests.Entities;

public sealed class TmuxLayoutSyntaxTests
{
    [Fact]
    public void Custom_layout_corpus_checks_syntax_and_minimum_cells_without_geometry()
    {
        using Stream input = typeof(TmuxLayoutSyntaxTests).Assembly
            .GetManifestResourceStream("LibTmux.Tests.Layouts.json")!;
        using JsonDocument corpus = JsonDocument.Parse(input);
        foreach (JsonElement item in corpus.RootElement.EnumerateArray())
        {
            string layout = item.GetProperty("layout").GetString()!;
            if (!layout.Contains(',', StringComparison.Ordinal))
            {
                continue;
            }

            string id = item.GetProperty("id").GetString()!;
            bool geometryRejected = id is "bad-inner-size" or "nested-invalid-width"
                or "nested-short-parent";
            bool expected = geometryRejected
                || item.GetProperty("expected_valid").GetProperty("3.7c").GetBoolean();
            bool accepted = TmuxLayoutSyntax.TryCountCells(layout, out int cells)
                && cells >= item.GetProperty("pane_count").GetInt32();
            Assert.True(accepted == expected, $"Unexpected syntax result for '{id}'.");
        }
    }

    [Theory]
    [InlineData("4294967295x0,0,0", true)]
    [InlineData("4294967296x0,0,0", false)]
    [InlineData("1x1,0,0,4294967295", true)]
    [InlineData("1x1,0,0,4294967296", false)]
    [InlineData("1x1,-1,0", false)]
    [InlineData("1x1,0,0\u001b", false)]
    public void Numeric_fields_and_trailing_bytes_are_bounded(string body, bool expected) =>
        Assert.Equal(expected, TmuxLayoutSyntax.TryCountCells(WithChecksum(body), out _));

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void Tree_depth_is_bounded(int depth, bool expected)
    {
        string body = string.Concat(Enumerable.Repeat("1x1,0,0{", depth))
            + "1x1,0,0" + new string('}', depth);
        Assert.Equal(expected, TmuxLayoutSyntax.TryCountCells(WithChecksum(body), out int cells));
        if (expected)
        {
            Assert.Equal(1, cells);
        }
    }

    private static string WithChecksum(string body)
    {
        int checksum = 0;
        foreach (char value in body)
        {
            checksum = (((checksum >> 1) + ((checksum & 1) << 15)) + value) & 0xffff;
        }

        return checksum.ToString("x4", CultureInfo.InvariantCulture) + "," + body;
    }
}
