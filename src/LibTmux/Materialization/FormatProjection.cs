using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text;

namespace LibTmux.Internal;

/// <summary>Names how a server enumerates its immediate children.</summary>
/// <param name="ListCommand">The tmux subcommand that lists the children.</param>
/// <param name="ChildIdAttribute">The format token identifying one child.</param>
/// <param name="FormatterPrefix">The prefix a server's own tokens carry.</param>
internal sealed record ServerProjectionDescriptor(
    string ListCommand,
    string ChildIdAttribute,
    string FormatterPrefix);

/// <summary>Describes how a tmux server projects its own children.</summary>
internal static class ServerProjection
{
    /// <summary>Gets the server's child enumeration descriptor.</summary>
    internal static ServerProjectionDescriptor Descriptor { get; } =
        new("list-sessions", "session_id", "server_");
}

/// <summary>
/// Selects the tmux format fields one list command can resolve on one tmux
/// version, and renders them as a self-describing <c>-F</c> template.
/// </summary>
/// <remarks>
/// A field is emitted only when the running tmux registers its token and the
/// list command's scope can resolve it. Emitting an unregistered token would
/// silently render empty, and emitting an out-of-scope token would mislead a
/// reader about what the format engine resolves for that command.
/// </remarks>
internal sealed class FormatProjection
{
    /// <summary>Separates the values of a framed row.</summary>
    /// <remarks>
    /// Randomized per process, not fixed: a caller-controlled name (a window
    /// or pane) could otherwise embed a fixed separator deliberately. It
    /// carries no <c>#</c>, so tmux cannot expand it, and a value containing
    /// it fails decode loudly instead of corrupting a field.
    /// <para>
    /// Length is bounded by tmux's <c>MAX_IMSGSIZE</c> command cap, shared
    /// with the generation guard on every entity command: 22 hex digits
    /// balance collision odds against that budget.
    /// </para>
    /// </remarks>
    internal static string RowSeparator { get; } = $"LT{Guid.NewGuid():N}"[..24];

    private readonly FrozenSet<string> _wireNames;

    private FormatProjection(
        string listCommand,
        TmuxVersion tmuxVersion,
        ReadOnlyCollection<FormatFieldDescriptor> fields,
        string template,
        bool hasPredicateMarker)
    {
        ListCommand = listCommand;
        TmuxVersion = tmuxVersion;
        Fields = fields;
        Template = template;
        HasPredicateMarker = hasPredicateMarker;
        _wireNames = fields
            .Select(static field => field.WireName)
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Gets the tmux list command this projection targets.</summary>
    internal string ListCommand { get; }

    /// <summary>Gets the tmux version this projection was gated against.</summary>
    internal TmuxVersion TmuxVersion { get; }

    /// <summary>Gets the projected fields in stable order.</summary>
    internal IReadOnlyList<FormatFieldDescriptor> Fields { get; }

    /// <summary>Gets the tmux <c>-F</c> template rendering every field.</summary>
    internal string Template { get; }

    /// <summary>Gets the number of separated values in one row.</summary>
    /// <remarks>
    /// Wire names are not sent. Both ends build the same projection from the
    /// same list command and tmux version, so a row is read positionally and
    /// the names stay off a wire that tmux caps at <c>MAX_IMSGSIZE</c>.
    /// </remarks>
    internal int FramedFieldCount => Fields.Count + (HasPredicateMarker ? 1 : 0);

    internal bool HasPredicateMarker { get; }

    /// <summary>Creates a projection for one list command and tmux version.</summary>
    /// <param name="listCommand">A tmux <c>list-*</c> subcommand.</param>
    /// <param name="tmuxVersion">The running tmux version.</param>
    /// <returns>The gated projection.</returns>
    internal static FormatProjection Create(string listCommand, TmuxVersion tmuxVersion) =>
        Create(listCommand, tmuxVersion, predicateFormat: null);

    internal static FormatProjection Create(string listCommand, TmuxVersion tmuxVersion, string? predicateFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listCommand);
        if (!tmuxVersion.IsValid)
        {
            throw new ArgumentException(
                "A valid tmux version is required.",
                nameof(tmuxVersion));
        }

        IReadOnlySet<string> scopes = FormatCatalog.GetScopesForListCommand(listCommand);
        ReadOnlyCollection<FormatFieldDescriptor> fields = Array.AsReadOnly(
            [.. FormatCatalog.ObjProjection.Where(
                field => field.Scopes.Overlaps(scopes)
                    && tmuxVersion.IsAtLeast(field.MinimumTmuxVersion))]);
        return new FormatProjection(
            listCommand,
            tmuxVersion,
            fields,
            RenderTemplate(fields) + (predicateFormat is null ? string.Empty : $"#{{?{predicateFormat},1,0}}" + RowSeparator),
            predicateFormat is not null);
    }

    /// <summary>Reports whether a wire name belongs to this projection.</summary>
    /// <param name="wireName">A tmux format token name.</param>
    /// <returns>True when the projection emits the field.</returns>
    internal bool Contains(string wireName) => _wireNames.Contains(wireName);

    private static string RenderTemplate(IReadOnlyList<FormatFieldDescriptor> fields)
    {
        var template = new StringBuilder();
        foreach (FormatFieldDescriptor field in fields)
        {
            // Each field is expanded exactly once: asking tmux for a byte
            // count too would expand it twice, and a field that changes
            // between the two expansions -- pane_current_command while a
            // shell settles, say -- desynchronizes every field after it.
            template.Append("#{");
            template.Append(field.WireName);
            template.Append('}');
            template.Append(RowSeparator);
        }

        return template.ToString();
    }
}
