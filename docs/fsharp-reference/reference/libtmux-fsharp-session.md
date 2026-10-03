## Session module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Starts queries confined to one session.

### Functions and values

<a name="panes"></a>

#### <code><span>Session.panes&#32;<span>session</span></span></code>

Queries the panes of every window in a session.

**Parameters:**

**session**: <code>Session</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Pane&gt;</span></code>

`IncompleteSnapshotException` The session was not read through a server.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L154)

<a name="windows"></a>

#### <code><span>Session.windows&#32;<span>session</span></span></code>

Queries the window placements in a session.

**Parameters:**

**session**: <code>Session</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Window&gt;</span></code>

`IncompleteSnapshotException` The session was not read through a server.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L151)
