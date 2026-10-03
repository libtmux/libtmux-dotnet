## Server module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Starts server reads and queries with the caller's cancellation token.

### Functions and values

<a name="capture"></a>

#### <code><span>Server.capture&#32;<span>cancellationToken&#32;depth&#32;server</span></span></code>

Returns a new server handle captured to the requested depth.

Acquisition is not atomic; retained handles do not refresh themselves.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**depth**: <code>SnapshotDepth</code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Server&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L22)

<a name="clients"></a>

#### <code><span>Server.clients&#32;<span>server</span></span></code>

Queries attached clients.

tmux narrows a filtered client listing only from tmux 3.4; older tmux lists every client.

**Parameters:**

**server**: <code>Server</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Client&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L19)

<a name="panes"></a>

#### <code><span>Server.panes&#32;<span>server</span></span></code>

Queries every pane.

**Parameters:**

**server**: <code>Server</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Pane&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L16)

<a name="sessions"></a>

#### <code><span>Server.sessions&#32;<span>server</span></span></code>

Queries every session.

Child windows and panes require an explicit capture at the corresponding depth.

**Parameters:**

**server**: <code>Server</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Session&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L10)

<a name="tryFindClient"></a>

#### <code><span>Server.tryFindClient&#32;<span>cancellationToken&#32;name&#32;server</span></span></code>

Returns the client with an exact name or None after a successful listing finds no match.

Connection, command and cancellation errors propagate unchanged.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**name**: <code>string</code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>Client&#32;option</span>&gt;</span></code>

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The client name is null, empty or whitespace.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L43)

<a name="tryFindPane"></a>

#### <code><span>Server.tryFindPane&#32;<span>cancellationToken&#32;id&#32;server</span></span></code>

Returns a pane or None after a successful lookup establishes absence.

Connection, command and cancellation errors propagate unchanged.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**id**: <code>PaneId</code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>Pane&#32;option</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L37)

<a name="tryFindSession"></a>

#### <code><span>Server.tryFindSession&#32;<span>cancellationToken&#32;id&#32;server</span></span></code>

Returns a session or None after a successful lookup establishes absence.

Connection, command and cancellation errors propagate unchanged.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**id**: <code>SessionId</code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>Session&#32;option</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L25)

<a name="tryFindWindow"></a>

#### <code><span>Server.tryFindWindow&#32;<span>cancellationToken&#32;id&#32;server</span></span></code>

Returns a window or None after a successful lookup establishes absence.

Connection, command and cancellation errors propagate unchanged.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**id**: <code>WindowId</code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>Window&#32;option</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L31)

<a name="windows"></a>

#### <code><span>Server.windows&#32;<span>server</span></span></code>

Queries window placements across all sessions.

A linked window appears once for each session it is linked into.

**Parameters:**

**server**: <code>Server</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Window&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L13)
