using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace LibTmux.Query;

/// <summary>Translates, compiles, and applies declarative query predicates.</summary>
/// <remarks>
/// One expression surface serves both sides: the same predicate translates to
/// the wire document and compiles to an in-memory delegate, so a filter cannot
/// mean one thing locally and another on the wire.
/// </remarks>
public static class QueryExtensions
{
    /// <summary>Translates an expression into a wire document.</summary>
    /// <typeparam name="T">The filtered element type.</typeparam>
    /// <param name="predicate">The predicate to translate.</param>
    /// <returns>The translated document.</returns>
    /// <exception cref="UnsupportedQueryExpressionException">
    /// The expression contains a node the query vocabulary does not cover.
    /// </exception>
    [RequiresDynamicCode(
        "Translating an expression evaluates its captured values, which needs runtime code generation.")]
    public static QueryDocument Translate<T>(Expression<Func<T, bool>> predicate) =>
        QueryTranslator.Translate(predicate);

    /// <summary>Compiles a document into an in-memory predicate.</summary>
    /// <typeparam name="T">The filtered element type.</typeparam>
    /// <param name="document">The translated document.</param>
    /// <returns>The compiled predicate.</returns>
    [RequiresUnreferencedCode(QueryInterpreter.TrimmingMessage)]
    public static Func<T, bool> Compile<T>(this QueryDocument document) =>
        QueryInterpreter.Compile<T>(document);

    /// <summary>Filters a snapshot with a declarative predicate.</summary>
    /// <typeparam name="T">The filtered element type.</typeparam>
    /// <param name="source">The captured elements.</param>
    /// <param name="predicate">The predicate to translate and apply.</param>
    /// <returns>The matching elements.</returns>
    [RequiresDynamicCode(
        "Translating an expression evaluates its captured values, which needs runtime code generation.")]
    [RequiresUnreferencedCode(QueryInterpreter.TrimmingMessage)]
    public static IReadOnlyList<T> Matching<T>(
        this IEnumerable<T> source,
        Expression<Func<T, bool>> predicate) =>
        source.Matching(Translate(predicate));

    /// <summary>Filters a snapshot with an already translated document.</summary>
    /// <typeparam name="T">The filtered element type.</typeparam>
    /// <param name="source">The captured elements.</param>
    /// <param name="document">The translated document.</param>
    /// <returns>The matching elements.</returns>
    [RequiresUnreferencedCode(QueryInterpreter.TrimmingMessage)]
    public static IReadOnlyList<T> Matching<T>(
        this IEnumerable<T> source,
        QueryDocument document)
    {
        ArgumentNullException.ThrowIfNull(source);
        Func<T, bool> compiled = document.Compile<T>();
        return [.. source.Where(compiled)];
    }
}
