namespace LibTmux.Query;

/// <summary>Describes text tmux searches for on a pane's visible rows.</summary>
/// <remarks>
/// tmux evaluates this itself, as <c>find-window -C</c> does: it reads only the
/// rows on screen, with trailing spaces removed, and nothing local can
/// recheck it. A pattern is a POSIX extended regular expression.
/// </remarks>
/// <param name="Text">The literal text or pattern.</param>
/// <param name="IsPattern">Whether the text is a regular expression.</param>
/// <param name="IgnoreCase">Whether letter case is ignored.</param>
internal sealed record PaneScreenSearch(string Text, bool IsPattern, bool IgnoreCase)
{
    /// <summary>Renders the <c>#{C:}</c> search.</summary>
    /// <exception cref="ArgumentException">The text cannot be written as a tmux format.</exception>
    internal string Render()
    {
        string flags = (IsPattern, IgnoreCase) switch
        {
            (true, true) => "/ri",
            (true, false) => "/r",
            (false, true) => "/i",
            _ => string.Empty,
        };

        // tmux copies a # run before [ verbatim as a style marker, so a pattern
        // must spell a literal # before [ as [#] or escape the bracket.
        string term = IsPattern ? Text : TmuxFilterRenderer.Glob(Text);
        string literal = TmuxFilterRenderer.Literal(term)
            ?? throw new ArgumentException(
                $"tmux cannot search for '{Text}': write # before [ as [#] or the bracket as \\[.",
                nameof(Text));
        return $"#{{C{flags}:{literal}}}";
    }
}
