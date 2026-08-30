using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace LibTmux.Query;

/// <summary>Translates, compiles, and applies declarative query predicates.</summary>
/// <remarks>
/// The same predicate translates to the portable document and compiles to an
/// in-memory delegate, so its stored form and local interpretation share one
/// meaning.
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

    /// <summary>Filters a snapshot with a cancellable translated document.</summary>
    /// <typeparam name="T">The filtered element type.</typeparam>
    /// <param name="source">The captured elements.</param>
    /// <param name="document">The translated document.</param>
    /// <param name="cancellationToken">Stops enumeration and predicate evaluation.</param>
    /// <returns>The matching elements.</returns>
    /// <remarks>
    /// Cancellation is observed between source elements and predicate nodes. A
    /// regex already running may take up to its one-second match timeout to stop.
    /// </remarks>
    [RequiresUnreferencedCode(QueryInterpreter.TrimmingMessage)]
    public static IReadOnlyList<T> Matching<T>(
        this IEnumerable<T> source,
        QueryDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        Func<T, bool> compiled = QueryInterpreter.Compile<T>(document, cancellationToken);
        List<T> matched = [];
        using IEnumerator<T> enumerator = source.GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
            {
                return matched;
            }

            bool accepted;
            try
            {
                accepted = compiled(enumerator.Current);
            }
            catch (RegexMatchTimeoutException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (accepted)
            {
                matched.Add(enumerator.Current);
            }
        }
    }
}
