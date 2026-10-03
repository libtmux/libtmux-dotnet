## Query module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Narrows and runs tmux queries, and filters captured objects locally.

### Functions and values

<a name="atMostOne"></a>

#### <code><span>Query.atMostOne&#32;<span>cancellationToken&#32;query</span></span></code>

Reads the sole match, or None when nothing matches; several matches raise.

None means tmux reported no match, so it suits finding an object or
 creating it when absent. It returns an option rather than a
 <code>Result</code>, so it publishes under NativeAOT.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**query**: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;'T&gt;</span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>'T&#32;option</span>&gt;</span></code>

Type parameters: 'T

[InvalidOperationException](https://learn.microsoft.com/dotnet/api/system.invalidoperationexception)tmux reported more than one match.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L252)

<a name="exactlyOne"></a>

#### <code><span>Query.exactlyOne&#32;<span>cancellationToken&#32;query</span></span></code>

Reads the sole match, or why there is not exactly one.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**query**: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;'T&gt;</span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span><a href="https://fsharp.github.io/fsharp-core-docs/reference/fsharp-core-fsharpresult-2">Result</a>&lt;<span>'T,&#32;<a href="../reference/libtmux-fsharp-cardinalityerror.md">CardinalityError</a></span>&gt;</span>&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L235)

<a name="list"></a>

#### <code><span>Query.list&#32;<span>cancellationToken&#32;query</span></span></code>

Reads the matching objects in tmux&#39;s listing order.

A filter over a relation reads a snapshot of only the sessions that can match.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**query**: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;'T&gt;</span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1">IReadOnlyList</a>&lt;'T&gt;</span>&gt;</span></code>

Type parameters: 'T

`TmuxVersionTooLowException` A raw client filter needs tmux 3.4.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L232)

<a name="matching"></a>

#### <code><span>Query.matching&#32;<span>filter&#32;source</span></span></code>

Filters captured objects locally, preserving input order and multiplicity.

**Parameters:**

**filter**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

**source**: <code><span>'T&#32;seq</span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1">IReadOnlyList</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L270)

<a name="showing"></a>

#### <code><span>Query.showing&#32;<span>search&#32;query</span></span></code>

Keeps panes whose visible rows show the searched text.

**Parameters:**

**search**: <code><a href="../reference/libtmux-fsharp-screensearch.md">ScreenSearch</a></code>

**query**: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Pane&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Pane&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L229)

<a name="tryExactlyOne"></a>

#### <code><span>Query.tryExactlyOne&#32;<span>cancellationToken&#32;query</span></span></code>

Reads the sole match, or None when there are none or several.

As FSharp.Core&#39;s <code>Seq.tryExactlyOne</code>, None does not say nothing
 matched. To find an object or create it when absent, use <code>atMostOne</code>.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**query**: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;'T&gt;</span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>'T&#32;option</span>&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L246)

<a name="where"></a>

#### <code><span>Query.where&#32;<span>filter&#32;query</span></span></code>

Adds a portable filter every result satisfies.

**Parameters:**

**filter**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

**query**: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;'T&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L222)

<a name="whereUnsafe"></a>

#### <code><span>Query.whereUnsafe&#32;<span>filter&#32;query</span></span></code>

Adds a raw tmux filter, which tmux evaluates and nothing rechecks.

A malformed or unknown token makes tmux keep no rows rather than report an error.

**Parameters:**

**filter**: <code>UnsafeTmuxFilter</code>

**query**: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;'T&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L225)
