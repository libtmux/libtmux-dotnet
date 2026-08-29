# LibTmux.Query.Json

JSON for [LibTmux](https://www.nuget.org/packages/LibTmux) query documents. The
core library does not reference `System.Text.Json`, so a caller who does not
want it does not get it.

> **Alpha.** The public API is not settled and can change between prereleases
> without notice, so pin an exact version.

```console
$ dotnet package add LibTmux.Query.Json --prerelease
```

## When you want this

A query in LibTmux is a *document*, not a lambda: an expression is translated
into a closed AST that can be checked, stored, logged, or sent somewhere else.
This package is how that document crosses a process boundary.

Reach for it when a filter is written in one place and evaluated in another —
a CLI that takes a filter argument, a service that accepts one over HTTP, a
tool that records what it queried.

## Use it

A query is written over the objects the library hands back, and becomes a
document that travels:

```csharp run
QueryDocument document = QueryExtensions.Translate<Session>(
    session => session.Name.StartsWith("build") && session.Attached);

string wire = QueryJson.Serialize(document);
QueryDocument parsed = QueryJson.Deserialize(wire);

// The document that came back means what the one that left meant.
Console.WriteLine(parsed == document);
```

`wire` is the versioned document, and it says what it is:

```json
{
  "schema": "libtmux-query",
  "version": 1,
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

The same document filters what you already hold, wherever it was written:

```csharp run
// However this arrived — an argument, a request body, a stored filter.
string received = QueryJson.Serialize(QueryExtensions.Translate<Session>(
    session => session.Name.StartsWith("build")));

IReadOnlyList<Session> sessions = await server.GetSessionsAsync(ct);
IReadOnlyList<Session> matched = sessions.Matching(QueryJson.Deserialize(received));

Console.WriteLine(matched.Count);
```

## What reading a document costs

Deserializing applies the limits in `QueryJsonLimits.V1`: document size,
nesting depth, node count, string length, and regex pattern length. A caller
may tighten those ceilings but cannot widen the v1 contract. The schema ships
in the package as `libtmux-query-v1.schema.json`.

```csharp run
Console.WriteLine($"depth {QueryJsonLimits.V1.MaximumDepth}, nodes {QueryJsonLimits.V1.MaximumNodes}");
```

## The field catalog is closed

`session_name`, `session_attached`, `session_id`, `session_windows`,
`window_name`, `window_id`, `window_panes`, `pane_id`, `pane_command`,
`client_id`, `client_name`, `client_control`.

You write these as the properties they are — `Session.Name`,
`Client.IsControlClient` — and the wire carries the tmux spelling.

A field outside it throws `UnsupportedQueryExpressionException` at translation
rather than falling back to filtering in memory, so a document that exists is
one tmux can answer.

## Related packages

| Package | Adds |
|---|---|
| [LibTmux](https://www.nuget.org/packages/LibTmux) | The client. Required. |
| [LibTmux.Workspace](https://www.nuget.org/packages/LibTmux.Workspace) | Sessions from tmuxp YAML |
| [LibTmux.Mcp](https://www.nuget.org/packages/LibTmux.Mcp) | A Model Context Protocol server, as a .NET tool |

Source, docs and issues: <https://github.com/libtmux/libtmux-dotnet>

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)
