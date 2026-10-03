using System.Runtime.Versioning;
using System.Text;

namespace LibTmux.UnitTests.Snapshots;

[UnsupportedOSPlatform("windows")]
public sealed class ServerMirrorFingerprintTests
{
    [Theory]
    [InlineData("client_activity_string")]
    [InlineData("client_written")]
    [InlineData("client_discarded")]
    [InlineData("window_offset_x")]
    [InlineData("window_offset_y")]
    [InlineData("saved_cursor_x")]
    [InlineData("saved_cursor_y")]
    [InlineData("synchronized_output_flag")]
    public void A_field_that_moves_with_use_does_not_change_the_fingerprint(string field) =>
        Assert.Equal(Print(Fields((field, "1"))), Print(Fields((field, "2"))));

    [Fact]
    public void A_field_that_says_what_changed_does_change_the_fingerprint() =>
        Assert.NotEqual(Print(Fields(("client_name", "/dev/pts/1"))), Print(Fields(("client_name", "/dev/pts/2"))));

    private static Dictionary<string, string?> Fields((string Name, string Value) varying) =>
        new(StringComparer.Ordinal)
        {
            ["client_name"] = "/dev/pts/1",
            ["client_session"] = "main",
            [varying.Name] = varying.Value,
        };

    private static string Print(IReadOnlyDictionary<string, string?> fields)
    {
        var text = new StringBuilder();
        ServerMirror.Append(text, 'c', fields);
        return text.ToString();
    }
}
