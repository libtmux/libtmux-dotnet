using System.Globalization;

namespace LibTmux.UnitTests.Identity;

public sealed class TypedIdentifierParsingTests
{
    [Fact]
    public void Typed_identifiers_parse_through_the_generic_interface()
    {
        // This is the whole point of IParsable: code that never names the type,
        // such as ASP.NET route binding, can still read one off the wire.
        Assert.Equal(new PaneId(3), ParseGenerically<PaneId>("%3"));
        Assert.Equal(new WindowId(12), ParseGenerically<WindowId>("@12"));
        Assert.Equal(new SessionId(0), ParseGenerically<SessionId>("$0"));

        Assert.False(TryParseGenerically<PaneId>("@3", out _));
        Assert.False(TryParseGenerically<WindowId>("nonsense", out _));
        Assert.False(TryParseGenerically<SessionId>(null, out _));
    }

    [Fact]
    public void Span_parsing_agrees_with_the_string_overload_and_round_trips()
    {
        foreach (string text in (string[])["%7", "@7", "$7"])
        {
            Assert.True(PaneId.TryParse(text, out PaneId pane) == PaneId.TryParse(text.AsSpan(), out _));
            Assert.True(WindowId.TryParse(text, out _) == WindowId.TryParse(text.AsSpan(), out _));
            Assert.True(SessionId.TryParse(text, out _) == SessionId.TryParse(text.AsSpan(), out _));
            if (pane.Value != 0)
            {
                Assert.Equal(text, pane.ToString());
            }
        }

        Assert.Equal(new PaneId(7), PaneId.Parse("%7".AsSpan()));
        Assert.Throws<FormatException>(() => PaneId.Parse("@7".AsSpan()));
    }

    private static T ParseGenerically<T>(string text)
        where T : IParsable<T> =>
        T.Parse(text, CultureInfo.InvariantCulture);

    private static bool TryParseGenerically<T>(string? text, out T? result)
        where T : IParsable<T> =>
        T.TryParse(text, CultureInfo.InvariantCulture, out result);
}
