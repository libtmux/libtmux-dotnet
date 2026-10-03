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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L121)

<a name="clients"></a>

#### <code><span>Server.clients&#32;<span>server</span></span></code>

Queries attached clients.

tmux narrows a filtered client listing only from tmux 3.4; older tmux lists every client.

**Parameters:**

**server**: <code>Server</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Client&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L26)

<a name="connect"></a>

#### <code><span>Server.connect&#32;<span>cancellationToken&#32;options</span></span></code>

Attaches to a server already listening on the socket the options name.

The core&#39;s <code>Server.ConnectAsync</code>; it never starts a server.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**options**: <code>ServerConnectionOptions</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Server&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L14)

<a name="createOwned"></a>

#### <code><span>Server.createOwned&#32;<span>cancellationToken&#32;options</span></span></code>

Starts a server on the socket the options name and owns it; disposing the scope stops it.

The core&#39;s <code>Server.CreateOwnedAsync</code>, named so F# need not qualify
 the type this module shares a name with. A server already listening on
 the default socket is refused rather than owned.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**options**: <code>ServerConnectionOptions</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;OwnedServerScope&gt;</span></code>

[InvalidOperationException](https://learn.microsoft.com/dotnet/api/system.invalidoperationexception) A server is already listening on the default socket.

`TmuxCommandException`tmux failed to say whether a server is listening, such as on a socket it may not open.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L11)

<a name="newSession"></a>

#### <code><span>Server.newSession&#32;<span>cancellationToken&#32;spec&#32;server</span></span></code>

Creates a session as described: its windows, and each window&#39;s splits.

<p class='fsdocs-para'>
 tmux gives a new session one window, so the first <code>WindowSpec</code> is
 that window: its name, command and directory go into the command that
 creates the session. tmux sets environment there for the whole session,
 so the first window&#39;s must be empty; put it in the session&#39;s. Each
 later spec creates a window of its own. A window&#39;s splits are made in
 order, each beside the pane before it.
 </p><p class='fsdocs-para'>
 Steps run one after another; a failure part way leaves what was already
 created, so kill the session by name to clean up.
 </p>

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**spec**: <code><a href="../reference/libtmux-fsharp-sessionspec.md">SessionSpec</a></code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Session&gt;</span></code>

The session, read again after its windows and panes exist.

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The session and its first window name different directories, or the
 first window sets an environment.

`TmuxSessionExistsException` The name is already taken.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L54)

<a name="panes"></a>

#### <code><span>Server.panes&#32;<span>server</span></span></code>

Queries every pane.

**Parameters:**

**server**: <code>Server</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Pane&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L23)

<a name="sessions"></a>

#### <code><span>Server.sessions&#32;<span>server</span></span></code>

Queries every session.

Child windows and panes require an explicit capture at the corresponding depth.

**Parameters:**

**server**: <code>Server</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Session&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L17)

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L142)

<a name="tryFindPane"></a>

#### <code><span>Server.tryFindPane&#32;<span>cancellationToken&#32;id&#32;server</span></span></code>

Returns a pane or None after a successful lookup establishes absence.

Connection, command and cancellation errors propagate unchanged.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**id**: <code>PaneId</code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>Pane&#32;option</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L136)

<a name="tryFindSession"></a>

#### <code><span>Server.tryFindSession&#32;<span>cancellationToken&#32;id&#32;server</span></span></code>

Returns a session or None after a successful lookup establishes absence.

Connection, command and cancellation errors propagate unchanged.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**id**: <code>SessionId</code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>Session&#32;option</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L124)

<a name="tryFindWindow"></a>

#### <code><span>Server.tryFindWindow&#32;<span>cancellationToken&#32;id&#32;server</span></span></code>

Returns a window or None after a successful lookup establishes absence.

Connection, command and cancellation errors propagate unchanged.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**id**: <code>WindowId</code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>Window&#32;option</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L130)

<a name="windows"></a>

#### <code><span>Server.windows&#32;<span>server</span></span></code>

Queries window placements across all sessions.

A linked window appears once for each session it is linked into.

**Parameters:**

**server**: <code>Server</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Window&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L20)

<a name="within"></a>

#### <code><span>Server.within&#32;<span>timeout&#32;server</span></span></code>

Returns the server with every command bounded by a timeout, for it and every handle taken from it.

The handle shares the connection, so nothing starts or is verified again.
 A command that outlasts the bound fails as <code>MayHaveRun</code>; a caller&#39;s
 own cancellation still reads as cancellation.

**Parameters:**

**timeout**: <code><a href="https://learn.microsoft.com/dotnet/api/system.timespan">TimeSpan</a></code>

**server**: <code>Server</code>

Returns: <code>Server</code>

[ArgumentOutOfRangeException](https://learn.microsoft.com/dotnet/api/system.argumentoutofrangeexception) The timeout does not run forward.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L29)
