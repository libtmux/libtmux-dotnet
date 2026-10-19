# LibTmux.Query.Json contracts

The rules behind the
[`LibTmux.Query.Json` README](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Query.Json#readme),
in full.

## Reading a document

Deserializing applies the limits in
[`QueryJsonLimits.Default`][api-query-json-queryjsonlimits-default]: document
size, nesting depth, node count, string length, and regex pattern length. A
caller may tighten those ceilings but cannot widen them. Schema version 2 is the
supported contract and ships as `libtmux-query-v2.schema.json`. Other versions
are rejected before their predicates are read.

Evaluating the result with [`Compile`][api-query-queryextensions-compile] or
[`Matching`][api-query-queryextensions-matching] resolves public properties by
name. Those methods warn trimmed callers to preserve that metadata. For a
document received from another trust boundary, use the cancellable `Matching`
overload with a deadline. It checks between source elements and predicate nodes;
a regex already running still has its separate one-second match ceiling.

## The field catalog

`Pane.CurrentCommand` and [`Pane.CurrentPath`][api-pane-currentpath] read
captured state without I/O. They throw
[`IncompleteSnapshotException`][api-incompletesnapshotexception] when the field
was never captured; equality distinguishes captured null from an empty string.
Portable string operations read null as empty, matching tmux format expansion.

`pane_current_path`, `window_active` and `window_index` query captured working
directories and session-relative window placements.
[`Window.IsActive`][api-window-isactive] and [`Window.Index`][api-window-index]
describe the placement this handle was captured through. Two handles for the
same linked window can disagree on both values.

The catalog also covers pane position, title, edge flags and tmux index, plus
window width and height. String predicates support ordinal equality, prefix,
suffix and containment, with explicit ordinal ignore-case variants. Native
tmux filtering only receives expressions the source planner can translate
exactly; the rest runs against captured values.

Collection predicates use native `Any` and `All`. Negate `Any` to require no
matches. `All` is true for an empty captured collection; an uncaptured
collection raises `IncompleteSnapshotException`.

The conditions inside one `Any` must match the same window placement.
Separate `Any` calls may match different windows. Filtering result membership
does not trim the captured relations used by later predicates.

Single relations serialize as a `related` node containing its relation
field and child predicate. They require a captured child. Unavailable values
raise an error without fetching data. `RequiredSnapshotDepth` includes every
referenced relation, including nested relations in either direction.

A field outside the catalog throws
[`UnsupportedQueryExpressionException`][api-unsupportedqueryexpressionexception]
at translation rather than falling back. The document is interpreted locally or
by an application that deliberately accepts this wire contract.

The catalog grows between alpha releases. A reader rejects a name it does not
know, so pin the same LibTmux version on both sides of a process boundary.

The catalog also includes pane state, exit status, history, terminal and start
command, and window alerts, flags and layout. Read the descriptors for the
installed package rather than assuming another alpha has the same fields.
Pin the same LibTmux version on both sides of a process boundary.

[api-incompletesnapshotexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-incompletesnapshotexception/
[api-pane-currentpath]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-currentpath/
[api-query-json-queryjsonlimits-default]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-json-queryjsonlimits-default/
[api-query-queryextensions-compile]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-compile/
[api-query-queryextensions-matching]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-matching/
[api-unsupportedqueryexpressionexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-unsupportedqueryexpressionexception/
[api-window-index]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-index/
[api-window-isactive]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-isactive/
