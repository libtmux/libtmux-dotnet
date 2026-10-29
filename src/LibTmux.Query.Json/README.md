<!-- libtmux-logo -->
<div align="center">
  <picture>
    <source srcset="assets/logo.svg" type="image/svg+xml">
    <img src="assets/logo.png" width="128" height="128" alt="libtmux for .NET Query.Json">
  </picture>
</div>
<!-- /libtmux-logo -->

<!-- libtmux-logo -->
<div align="center">
<!-- /libtmux-logo -->

# LibTmux.Query.Json

[![LibTmux.Query.Json](https://img.shields.io/nuget/v/LibTmux.Query.Json?label=LibTmux.Query.Json)](https://www.nuget.org/packages/LibTmux.Query.Json)
[![libtmux.org](https://img.shields.io/badge/docs-libtmux.org-0b7285)](https://libtmux.org/en/csharp/)

JSON for [LibTmux](https://www.nuget.org/packages/LibTmux)
[query documents][api-query-querydocument]. The core library does not reference
`System.Text.Json`, so a caller who does not want it does not get it.

<!-- libtmux-logo -->
</div>
<!-- /libtmux-logo -->

A query in LibTmux is a document: an expression translated into a closed tree
that can be checked, stored, logged, or sent somewhere else.

This package carries that document across a process boundary. It suits a CLI
that takes a filter argument, a service that accepts one over HTTP, and a tool
that records what it queried.

Add it to your project:

```console
$ dotnet add package LibTmux.Query.Json
```

> **Releases.** Releases are numbered `0.1.N`, and each release increments `N`.
> The API is not settled: any release may change or remove exported identifiers,
> and the changelog marks each break. Pin an exact version to avoid one.

**Contents** — [Quick start](#quick-start) ·
[Send a filter](#send-a-filter-to-another-process) ·
[Limits](#limit-what-a-received-document-costs) ·
[Fields](#choose-fields-and-relations) · [Leaves out](#what-it-leaves-out) ·
[Support](#support-and-status)

## Quick start

Write a filter over the objects LibTmux returns, send it as text, and read it
back. The block needs no tmux server:

```csharp run
QueryDocument document = QueryExtensions.Translate<Session>(
    session => session.Name.StartsWith("build", StringComparison.Ordinal)
        && session.Attached);

string wire = QueryJson.Serialize(document);
QueryDocument parsed = QueryJson.Deserialize(wire);

// The document that came back means what the one that left meant.
Console.WriteLine(parsed == document);
```

It prints `True`: the document that came back means what the one that left
meant.

## Send a filter to another process

### [`QueryJson.Serialize`][api-query-json-queryjson-serialize]: the document as versioned text

`wire` in the quick start is this document. It names its schema and version, so
a reader can reject one it does not know:

```json
{
  "schema": "libtmux-query",
  "version": 2,
  "target": "session",
  "predicate": {
    "kind": "and",
    "operands": [
      {
        "kind": "comparison",
        "operator": "startsWithOrdinal",
        "left": {
          "kind": "field",
          "target": "session",
          "wireName": "session_name"
        },
        "right": {
          "kind": "constant",
          "value": { "kind": "string", "value": "build" }
        }
      },
      {
        "kind": "field",
        "target": "session",
        "wireName": "session_attached"
      }
    ]
  }
}
```

### [`QueryJson.Deserialize`][api-query-json-queryjson-deserialize]: apply a filter you received

The same document filters what you already hold, wherever it was written:

```csharp run
// However this arrived — an argument, a request body, a stored filter.
string received = QueryJson.Serialize(QueryExtensions.Translate<Session>(
    session => session.Name.StartsWith("build", StringComparison.Ordinal)));

using var queryBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
queryBudget.CancelAfter(TimeSpan.FromSeconds(1));
IReadOnlyList<Session> sessions = await server.GetSessionsAsync(ct);
IReadOnlyList<Session> matched = sessions.Matching(
    QueryJson.Deserialize(received),
    queryBudget.Token);

Console.WriteLine(matched.Count);
```

It prints the number of sessions whose name starts with `build`.

For a document from another trust boundary, use the cancellable
[`QueryExtensions.Matching`][api-query-queryextensions-matching] overload with a
deadline, as the block does.

## Limit what a received document costs

### [`QueryJsonLimits.Default`][api-query-json-queryjsonlimits-default]: ceilings on what a reader accepts

```csharp run
Console.WriteLine($"depth {QueryJsonLimits.Default.MaximumDepth}, nodes {QueryJsonLimits.Default.MaximumNodes}");
```

It prints the nesting depth and node count the defaults allow.

- Deserializing applies limits on document size, nesting depth, node count,
  string length, and regex pattern length. A caller may tighten them and cannot
  widen them.
- Schema version 2 is the supported contract, shipped as
  `libtmux-query-v2.schema.json`. Other versions are rejected before their
  predicates are read.
- [`Compile`][api-query-queryextensions-compile] and
  [`Matching`][api-query-queryextensions-matching] resolve public properties by
  name, so they warn trimmed callers to preserve that metadata.

## Choose fields and relations

### [`QueryFieldCatalog`][api-query-queryfieldcatalog]: the closed set of fields

`QueryFieldCatalog.GetFields(QueryTarget.Pane)` lists the fields, their native
properties, and the operators each accepts. Write predicates against properties
such as [`Session.Name`][api-session-name],
[`Client.IsControlClient`][api-client-iscontrolclient], and
[`Pane.CurrentCommand`][api-pane-currentcommand]:

```csharp run
QueryDocument paths = QueryExtensions.Translate<Pane>(
    pane => pane.CurrentPath == "/srv/api");
QueryDocument restoredPaths = QueryJson.Deserialize(QueryJson.Serialize(paths));
Console.WriteLine(restoredPaths.Version);
```

It prints the document's schema version.

- The wire name `pane_command` binds to tmux's `pane_current_command`.
- [`Pane.CurrentPath`][api-pane-currentpath] and `Pane.CurrentCommand` read
  captured state without I/O. Reading one that was never captured throws
  [`IncompleteSnapshotException`][api-incompletesnapshotexception].
- A field outside the catalog throws
  [`UnsupportedQueryExpressionException`][api-unsupportedqueryexpressionexception]
  at translation.
- The catalog grows between releases, and a reader rejects a name it does
  not know, so pin the same LibTmux version on both sides of a process boundary.

### Pane size

Pane dimensions support numeric comparisons over the same captured objects:

```csharp run
QueryDocument widePanes = QueryExtensions.Translate<Pane>(
    pane => pane.Width >= 50 && pane.Height >= 20);
QueryDocument restoredDimensions = QueryJson.Deserialize(QueryJson.Serialize(widePanes));
Server capturedPanes = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, ct);
Console.WriteLine(capturedPanes.Panes.Matching(restoredDimensions).Count);
```

It prints how many captured panes are at least 50 columns by 20 rows.

### `Any` and `All`: filters over a relation

Collection predicates use `Any` and `All`. Negate `Any` to require no matches.
`All` is true for an empty captured collection:

```csharp run
QueryDocument linkedEditors = QueryExtensions.Translate<Session>(
    session => session.Windows.Any(window =>
        window.IsActive && window.Name == "editor"
        && window.LinkedSessions.Any(linked => linked.Name == "work")));
Console.WriteLine(linkedEditors.RequiredSnapshotDepth);
```

It prints the snapshot depth the document needs.

- The conditions inside one `Any` must match the same window placement. Separate
  `Any` calls may match different windows.
- [`Window.IsActive`][api-window-isactive] and
  [`Window.Index`][api-window-index] describe the placement the handle was
  captured through. Two handles for the same linked window can disagree on both.

### Single relations: ordinary property navigation

```csharp run
QueryDocument selectedEditor = QueryExtensions.Translate<Session>(
    session => session.ActiveWindow.Value.Name == "editor");
Console.WriteLine(QueryJson.Serialize(selectedEditor));
```

It prints the serialized document.

| Relation | Property | Required capture |
| --- | --- | --- |
| `session_active_window` | `Session.ActiveWindow.Value` | Windows |
| `session_active_pane` | `Session.ActivePane.Value` | Panes |
| `session_panes` | [`Session.Panes`][api-session-panes] | Panes |
| `window_session` | [`Window.Session`][api-window-session] | Windows |
| `window_active_pane` | `Window.ActivePane.Value` | Panes |
| `window_linked_sessions` | [`Window.LinkedSessions`][api-window-linkedsessions] | Windows |
| `pane_window` | [`Pane.Window`][api-pane-window] | Panes |
| `pane_session` | [`Pane.Session`][api-pane-session] | Panes |

A single relation needs a captured child; an unavailable value raises an error
without fetching data.

## Related packages

| Package | Adds |
|---|---|
| [LibTmux](https://www.nuget.org/packages/LibTmux) | The client. Required. |
| [LibTmux.Workspace](https://www.nuget.org/packages/LibTmux.Workspace) | Sessions from tmuxp YAML |
| [LibTmux.Mcp](https://www.nuget.org/packages/LibTmux.Mcp) | A Model Context Protocol server, as a .NET tool |

## What it leaves out

- **A query language.** Filters are C# expressions over LibTmux objects, and the
  document is their serialized form.
- **Unbounded input.** A reader enforces size, depth, node, string, and regex
  limits before it evaluates anything.
- **Guessing at fields.** A document that names a field outside the catalog is
  rejected, and so is a schema version other than 2.

## Support and status

- **Releases.** Releases are numbered `0.1.N`, and each release increments `N`.
  The API is not settled: any release may change or remove exported identifiers,
  and the changelog marks each break. Pin an exact version to avoid one.
- **Dependencies.** [LibTmux](https://www.nuget.org/packages/LibTmux) and
  `System.Text.Json`.
- **tmux and .NET.** The tmux versions LibTmux supports, on net8.0 and net10.0.
- **Systems.** Linux, including WSL. CI also runs macOS arm64 and builds on
  Windows x64.

## Documentation

- [The full contracts](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.Query.Json/docs/contracts.md):
  limits, the field catalog, captured state, and relations
- [`QueryJson`][api-query-json-queryjson] and
  [`QueryDocument`][api-query-querydocument] on libtmux.org
- Source:
  [`src/LibTmux.Query.Json`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Query.Json);
  issues and history on [GitHub](https://github.com/libtmux/libtmux-dotnet)

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)

[api-client-iscontrolclient]: https://libtmux.org/en/csharp/latest/reference/libtmux-client-iscontrolclient/
[api-incompletesnapshotexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-incompletesnapshotexception/
[api-pane-currentcommand]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-currentcommand/
[api-pane-currentpath]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-currentpath/
[api-pane-session]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-session/
[api-pane-window]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-window/
[api-query-json-queryjson]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-json-queryjson/
[api-query-json-queryjson-deserialize]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-json-queryjson-deserialize/
[api-query-json-queryjson-serialize]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-json-queryjson-serialize/
[api-query-json-queryjsonlimits-default]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-json-queryjsonlimits-default/
[api-query-querydocument]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-querydocument/
[api-query-queryextensions-compile]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-compile/
[api-query-queryextensions-matching]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-matching/
[api-query-queryfieldcatalog]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryfieldcatalog/
[api-session-name]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-name/
[api-session-panes]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-panes/
[api-unsupportedqueryexpressionexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-unsupportedqueryexpressionexception/
[api-window-index]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-index/
[api-window-isactive]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-isactive/
[api-window-linkedsessions]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-linkedsessions/
[api-window-session]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-session/
