<!-- libtmux-logo -->
<p align="center">
  <picture>
    <source srcset="assets/logo.svg" type="image/svg+xml">
    <img src="https://raw.githubusercontent.com/libtmux/libtmux-dotnet/master/src/LibTmux.Query.Json/assets/logo.png" width="128" height="128" alt="libtmux for C# / .NET">
  </picture>
</p>
<!-- /libtmux-logo -->

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
    session => session.Name.StartsWith("build", StringComparison.Ordinal)
        && session.Attached);

string wire = QueryJson.Serialize(document);
QueryDocument parsed = QueryJson.Deserialize(wire);

// The document that came back means what the one that left meant.
Console.WriteLine(parsed == document);
```

`wire` is the versioned document, and it says what it is:

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

## What reading a document costs

Deserializing applies the limits in `QueryJsonLimits.Default`: document size,
nesting depth, node count, string length, and regex pattern length. A caller
may tighten those ceilings but cannot widen them. Schema version 2 is the
supported contract and ships as `libtmux-query-v2.schema.json`. Other versions
are rejected before their predicates are read.

Evaluating the result with `Compile` or `Matching` resolves public properties
by name. Those methods warn trimmed callers to preserve that metadata.
For a document received from another trust boundary, use the cancellable
`Matching` overload with a deadline. It checks between source elements and
predicate nodes; a regex already running still has its separate one-second
match ceiling.

```csharp run
Console.WriteLine($"depth {QueryJsonLimits.Default.MaximumDepth}, nodes {QueryJsonLimits.Default.MaximumNodes}");
```

## The field catalog is closed

Sessions: `session_name`, `session_attached`, `session_id`, `session_windows`.
Windows: `window_name`, `window_id`, `window_index`, `window_width`,
`window_height`, `window_panes`, `window_active`, `window_zoomed_flag`,
`window_bell_flag`, `window_activity_flag`, `window_silence_flag`,
`window_flags`, `window_layout`. Panes:
`pane_id`, `pane_command`, `pane_index`, `pane_title`, `pane_current_path`,
`pane_width`, `pane_height`, `pane_left`, `pane_top`, `pane_at_top`,
`pane_at_bottom`, `pane_at_left`, `pane_at_right`, `pane_active`, `pane_dead`,
`pane_dead_status`, `pane_in_mode`, `pane_pid`, `pane_synchronized`,
`history_size`, `pane_tty`, `pane_start_command`. Clients:
`client_id`, `client_name`, `client_control_mode`.

You write these as the properties they are, such as `Session.Name`,
`Pane.Width`, `Client.IsControlClient` and `Pane.CurrentCommand`. The wire name
`pane_command` binds to the captured tmux `pane_current_command` value; every
other name is the tmux format it reads.

```csharp run
QueryDocument paths = QueryExtensions.Translate<Pane>(
    pane => pane.CurrentPath == "/srv/api");
QueryDocument restoredPaths = QueryJson.Deserialize(QueryJson.Serialize(paths));
Console.WriteLine(restoredPaths.Version);
```

Pane properties read captured state without I/O. They throw
`IncompleteSnapshotException` when the field was never captured; captured
null and empty-string values remain distinct during local matching.

`pane_current_path`, `window_active` and `window_index` query captured
working directories and session-relative window placements.
`Window.IsActive` and `Window.Index` describe the placement this handle was
captured through. Two handles for the same linked window can disagree on
both values.

Collection predicates use native `Any` and `All`. Negate `Any` to require no
matches. `All` is true for an empty captured collection; an uncaptured
collection raises `IncompleteSnapshotException`.

```csharp run
QueryDocument linkedEditors = QueryExtensions.Translate<Session>(
    session => session.Windows.Any(window =>
        window.IsActive && window.Name == "editor"
        && window.LinkedSessions.Any(linked => linked.Name == "work")));
Console.WriteLine(linkedEditors.RequiredSnapshotDepth);
```

The conditions inside one `Any` must match the same window placement.
Separate `Any` calls may match different windows. Filtering result membership
does not trim the captured relations used by later predicates.

Single relations use ordinary property navigation:

```csharp run
QueryDocument selectedEditor = QueryExtensions.Translate<Session>(
    session => session.ActiveWindow.Value.Name == "editor");
Console.WriteLine(QueryJson.Serialize(selectedEditor));
```

| Relation | Property | Required capture |
| --- | --- | --- |
| `session_active_window` | `Session.ActiveWindow.Value` | Windows |
| `session_active_pane` | `Session.ActivePane.Value` | Panes |
| `session_panes` | `Session.Panes` | Panes |
| `window_session` | `Window.Session` | Windows |
| `window_active_pane` | `Window.ActivePane.Value` | Panes |
| `window_linked_sessions` | `Window.LinkedSessions` | Windows |
| `pane_window` | `Pane.Window` | Panes |
| `pane_session` | `Pane.Session` | Panes |

Single relations serialize as a `related` node containing its relation
field and child predicate. They require a captured child. Unavailable values
raise an error without fetching data. `RequiredSnapshotDepth` includes every
referenced relation, including nested relations in either direction.

The catalog grows between alpha releases. A reader rejects a name it does not
know, so pin the same LibTmux version on both sides of a process boundary.

A field outside the catalog throws
`UnsupportedQueryExpressionException` at translation rather than falling back.
The document is interpreted locally or by an
application that deliberately accepts this wire contract.

## Related packages

| Package | Adds |
|---|---|
| [LibTmux](https://www.nuget.org/packages/LibTmux) | The client. Required. |
| [LibTmux.Workspace](https://www.nuget.org/packages/LibTmux.Workspace) | Sessions from tmuxp YAML |
| [LibTmux.Mcp](https://www.nuget.org/packages/LibTmux.Mcp) | A Model Context Protocol server, as a .NET tool |

Source, docs and issues: <https://github.com/libtmux/libtmux-dotnet>

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)
