# Queries with LibTmux.FSharp

Use `Seq.filter` for application-specific work over a captured snapshot. Use
`Filter` when the condition must become a portable `QueryDocument`.

<!-- fsharp-snippet: MatchingSessions -->
```fsharp
open LibTmux
open LibTmux.FSharp

let editorSessions =
    Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand
    |> Filter.any WindowFields.panes
    |> Filter.any SessionFields.windows

let matchingSessions (captured: Server) =
    captured.Sessions |> Query.matching editorSessions
```
<!-- endfsharp-snippet -->

`Query.matching` is local and materialized. It preserves input order and
placement multiplicity. It does not send a tmux format filter. Capture to the
document's `RequiredSnapshotDepth` before matching; incomplete relationships
raise `IncompleteSnapshotException` rather than behaving as empty.

Only descriptors in [supported query fields](supported-query-fields.md) are
portable. `Pane.currentPath`, window placement, geometry, and parent links are
ordinary captured-object data for native F# predicates.
