# LibTmux.FSharp

F# task functions and pure snapshot queries over LibTmux's existing handles.

<!-- fsharp-contract: golden -->
```fsharp
open System.Threading
open LibTmux
open LibTmux.FSharp

let readEditorSessionNamesAsync
    (cancellationToken: CancellationToken)
    (server: Server)
    =
    task {
        let hasEditor =
            Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand
            |> Filter.any WindowFields.panes
            |> Filter.any SessionFields.windows

        let document = Filter.toDocument hasEditor
        let! captured = server |> Server.capture cancellationToken document.RequiredSnapshotDepth

        return
            captured.Sessions
            |> Query.matching hasEditor
            |> Seq.map (fun session -> session.Name)
            |> Seq.toList
    }
```

`Server.capture` performs the only I/O in this example. `Query.matching`
evaluates the portable filter against captured objects, materializes an
`IReadOnlyList`, and preserves input order and placement multiplicity.

## Native F# queries

Use ordinary F# sequences for predicates that do not need portable query
documents. Captured-field access and sequence processing do not contact tmux.

```fsharp
open System
open LibTmux
open LibTmux.FSharp

let editorPaneIds (captured: Server) =
    captured.Panes
    |> Seq.filter (fun pane ->
        String.Equals(pane.CurrentCommand, "nvim", StringComparison.Ordinal))
    |> Seq.map (fun pane -> pane.Id)
    |> Seq.toList
```

`Pane.currentPath` and `Pane.currentCommand` provide option-valued adapters
when an application needs to distinguish a captured null from an empty string.
They preserve `IncompleteSnapshotException` when the field was not captured.

## Portable filters

Use `Seq.filter` for ordinary predicates over captured objects. `Filter`
constructs portable conditions from typed descriptors and delegates translation
and evaluation to the core query engine. `Query.matching` materializes an
`IReadOnlyList` in input order; `Filter.toPredicate` compiles once for lazy
native filtering. Neither operation contacts tmux.

String constants use ordinal semantics. Use `Filter.isNull` for a captured null
string; an uncaptured field still raises `IncompleteSnapshotException`.
`allOf`, `anyOf` and `oneOf` require nonempty lists. `Filter.toDocument`
returns the core `QueryDocument`, which the optional `LibTmux.Query.Json`
package can serialize. Native tmux format filters remain a separate core
facility; portable filters do not push themselves to tmux.

```fsharp
open LibTmux
open LibTmux.FSharp

let editor =
    Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand

let acceptsEditor = editor |> Filter.toPredicate

let firstEditor (captured: Server) =
    captured.Panes
    |> Seq.filter acceptsEditor
    |> Seq.tryHead
```

## Lookup and cardinality

Use `option` for a completed lookup that found no entity. Operational and
cancellation failures still propagate from the core task. Use
`Selection.exactlyOne` when zero and multiple matches need different handling.

```fsharp
open System.Threading
open LibTmux
open LibTmux.FSharp

let tryFindPaneAsync (cancellationToken: CancellationToken) (server: Server) =
    task {
        let! pane = server |> Server.tryFindPane cancellationToken (PaneId 42)

        return pane
    }

let selectCapturedPane (captured: Server) =
    captured.Panes
    |> Selection.exactlyOne
```

`Selection.exactlyOne` reads at most two yielded elements and disposes the
enumerator. It returns `Error NoMatches` or `Error MultipleMatches`; it does
not use `Seq.tryExactlyOne`, which combines those outcomes into `None`.

## Capture state and cancellation

`Snapshot.relation` and `Snapshot.value` distinguish captured empty data from
data that the selected snapshot depth did not acquire. A portable condition
whose required depth exceeds the snapshot raises `IncompleteSnapshotException`;
it never treats the relation as empty. Pass the caller's token explicitly to
every task function. Cancellation can stop a wait after tmux has received a
mutation, so it does not roll that mutation back or make retry safe.

## Compatibility

The package targets .NET 8 and .NET 10 and shares the matching core package
version. Its installed-package consumer currently has local real-tmux evidence
on tmux 3.7d. A Linux NativeAOT consumer publishes and runs the static snapshot
and native `Seq` projection route on both target frameworks. It does not call
`Selection.exactlyOne`: FSharp.Core 10.1.302 emits trim and AOT diagnostics
when a NativeAOT consumer reaches its `Result` return type. Treat cardinality
as unsupported in NativeAOT until that dependency can be proved clean. The
required cross-version tmux matrix remains unfinished for this alpha package.
