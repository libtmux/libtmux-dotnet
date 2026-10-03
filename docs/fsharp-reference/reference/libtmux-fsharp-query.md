## Query module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Applies portable filters locally to captured objects.

### Functions and values

<a name="matching"></a>

#### <code><span>Query.matching&#32;<span>filter&#32;source</span></span></code>

Materializes matching elements, preserving input order and multiplicity.

**Parameters:**

**filter**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

**source**: <code><span>'T&#32;seq</span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1">IReadOnlyList</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L147)

<a name="matchingWithCancellation"></a>

#### <code><span>Query.matchingWithCancellation&#32;<span>cancellationToken&#32;filter&#32;source</span></span></code>

Materializes matches with cancellation between elements and predicate nodes.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**filter**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

**source**: <code><span>'T&#32;seq</span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1">IReadOnlyList</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L150)
