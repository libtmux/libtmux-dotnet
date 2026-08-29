using LibTmux.Query;

namespace LibTmux.UnitTests.Query;

public sealed class QueryTranslationSafetyTests
{
    private sealed record Row(string SessionName);

    private sealed class SideEffectSource
    {
        internal int Reads { get; private set; }

        internal string Value
        {
            get
            {
                Reads++;
                return "dev";
            }
        }

        internal string Read()
        {
            Reads++;
            return "dev";
        }
    }

    [Fact]
    public void Translation_rejects_method_constants_without_invoking_them()
    {
        var source = new SideEffectSource();

        Exception? error = Record.Exception(
            () => QueryExtensions.Translate<Row>(row => row.SessionName == source.Read()));

        Assert.Equal(0, source.Reads);
        Assert.IsType<UnsupportedQueryExpressionException>(error);
    }

    [Fact]
    public void Translation_rejects_property_constants_without_reading_them()
    {
        var source = new SideEffectSource();

        Exception? error = Record.Exception(
            () => QueryExtensions.Translate<Row>(row => row.SessionName == source.Value));

        Assert.Equal(0, source.Reads);
        Assert.IsType<UnsupportedQueryExpressionException>(error);
    }

    [Fact]
    public void Translation_freezes_a_captured_local()
    {
        string expected = "dev";

        QueryDocument document = QueryExtensions.Translate<Row>(
            row => row.SessionName == expected);

        StringNode equality = Assert.IsType<StringNode>(document.Predicate);
        Assert.Equal(
            new StringConstant("dev"),
            Assert.IsType<ConstantNode>(equality.Right).Value);
    }
}
