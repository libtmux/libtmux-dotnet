# .NET interoperation

The companion is built on
[LibTmux](https://github.com/libtmux/libtmux-dotnet/) in the same `libtmux`
organization and maintained by the same primary author. It uses the existing
entities, IDs, requests, exceptions, snapshots, and query documents. Pass a
`Server`, `Session`, `Window`, or `Pane` between F# and C# without conversion.

`Task<'T>` remains the default asynchronous contract. Pass the cancellation
token to the façade function explicitly and preserve core exceptions. Use
`option` only for a completed lookup that found no entity; use
`Selection.exactlyOne` when zero and multiple local matches need different
outcomes.

`LibTmux.Query.Json` remains optional. Add it only when a portable filter must
cross a process or language boundary. It serializes the core `QueryDocument`;
the F# package does not define a second format.

<!-- fsharp-snippet: PortableFilterJson run -->
```fsharp run
open LibTmux
open LibTmux.FSharp
open LibTmux.Query.Json

let encodeEditorPaneFilter () =
    Filter.oneOf [ "nvim"; "vim" ] PaneFields.currentCommand
    |> Filter.toDocument
    |> QueryJson.Serialize

let decodeFilter json = QueryJson.Deserialize json
```
<!-- endfsharp-snippet -->

Validate and apply the decoded document with the core query APIs. A JSON round
trip does not turn a portable filter into a tmux format expression.
