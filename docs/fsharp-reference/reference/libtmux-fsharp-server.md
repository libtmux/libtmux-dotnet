## Server module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Starts server reads and queries with the caller&#39;s cancellation token.

<p class='fsdocs-para'>
 Each function takes the value it acts on last. One that runs a tmux command
 also takes the cancellation token first and returns a <code>Task</code>, so a
 call reads <code>Server.tryFindSession token id server</code> or
 <code>server |&gt; Server.tryFindSession token id</code>, and
 <a href="../reference/libtmux-fsharp-tmuxasync.md#awaitTask">TmuxAsync.awaitTask</a> turns the task into an
 <code>Async</code>. The server is the core <code>LibTmux.Server</code> handle, which is
 immutable and safe to share.
 </p><p class='fsdocs-para'><b>What is on here</b></p><p class='fsdocs-para'>
 A tmux server does a great many things, but this module covers the part an
 F# caller reaches first. The rest lives in the sibling modules and on the
 core handle. The members fall into a few groups:
 </p><p class='fsdocs-para'><b>Connecting.</b> Find a server already listening on the socket the options
 name, which never starts one, or start one there and own it, which stops it
 when the scope is disposed and refuses a server already listening on the
 default socket. Bounding the server by a timeout makes a command that
 outlasts it fail as <code>MayHaveRun</code>. Entering control mode opens a control
 client that reports what tmux does.
 </p><p class='fsdocs-para'><a href="../reference/libtmux-fsharp-server.md#connect">Server.connect</a>,
 <a href="../reference/libtmux-fsharp-server.md#createOwned">Server.createOwned</a>,
 <a href="../reference/libtmux-fsharp-server.md#within">Server.within</a>,
 <a href="../reference/libtmux-fsharp-control.md#enter">Control.enter</a></p><p class='fsdocs-para'><b>Finding one thing.</b> Look one up by id, or a client by its exact name;
 a client name that is null, empty or whitespace throws
 <a href="https://learn.microsoft.com/dotnet/api/system.argumentexception">ArgumentException</a>. Each returns <code>None</code> after a
 successful lookup establishes absence, and connection, command and
 cancellation errors propagate unchanged.
 </p><p class='fsdocs-para'><a href="../reference/libtmux-fsharp-server.md#tryFindSession">Server.tryFindSession</a>,
 <a href="../reference/libtmux-fsharp-server.md#tryFindWindow">Server.tryFindWindow</a>,
 <a href="../reference/libtmux-fsharp-server.md#tryFindPane">Server.tryFindPane</a>,
 <a href="../reference/libtmux-fsharp-server.md#tryFindClient">Server.tryFindClient</a></p><p class='fsdocs-para'><b>Listing everything.</b> Describe a listing of sessions, windows, panes, or
 clients as a <a href="../reference/libtmux-fsharp-query-1.md">Query</a>, which reads nothing
 until it is run; every run reads tmux again. A linked window appears once
 for each session it is linked into, and tmux narrows a filtered client
 listing only from tmux 3.4, so older tmux lists every client. Capturing
 reads the tree to the depth asked for and returns a new handle whose
 captured relations answer without another command.
 </p><p class='fsdocs-para'><a href="../reference/libtmux-fsharp-server.md#sessions">Server.sessions</a>,
 <a href="../reference/libtmux-fsharp-server.md#windows">Server.windows</a>,
 <a href="../reference/libtmux-fsharp-server.md#panes">Server.panes</a>,
 <a href="../reference/libtmux-fsharp-server.md#clients">Server.clients</a>,
 <a href="../reference/libtmux-fsharp-server.md#capture">Server.capture</a>,
 <a href="../reference/libtmux-fsharp-snapshot.md#relation">Snapshot.relation</a></p><p class='fsdocs-para'><b>Querying things and getting around.</b> Narrow a listing with the
 <a href="../reference/libtmux-fsharp-query.md">Query</a> module and a
 <a href="../reference/libtmux-fsharp-filter-1.md">Filter</a> built from
 <a href="../reference/libtmux-fsharp-sessionfields.md">SessionFields</a>,
 <a href="../reference/libtmux-fsharp-windowfields.md">WindowFields</a>,
 <a href="../reference/libtmux-fsharp-panefields.md">PaneFields</a>, or
 <a href="../reference/libtmux-fsharp-clientfields.md">ClientFields</a>; a raw filter handed to tmux
 keeps no rows, rather than failing, when it is malformed, and a raw client
 filter throws TmuxVersionTooLowException on tmux
 older than 3.4. Running the query lists its matches; to read a single
 one, <code>exactlyOne</code> says why there is not exactly one, <code>tryExactlyOne</code>
 answers <code>None</code> for none or several, and <code>atMostOne</code> answers
 <code>None</code> only for none and throws for several. Moving between levels
 starts a query inside a session or window, and the active pane is read from
 tmux.
 </p><p class='fsdocs-para'><a href="../reference/libtmux-fsharp-query.md#where">Query.where</a>,
 <a href="../reference/libtmux-fsharp-query.md#whereUnsafe">Query.whereUnsafe</a>,
 <a href="../reference/libtmux-fsharp-query.md#showing">Query.showing</a>,
 <a href="../reference/libtmux-fsharp-query.md#list">Query.list</a>,
 <a href="../reference/libtmux-fsharp-query.md#exactlyOne">Query.exactlyOne</a>,
 <a href="../reference/libtmux-fsharp-query.md#tryExactlyOne">Query.tryExactlyOne</a>,
 <a href="../reference/libtmux-fsharp-query.md#atMostOne">Query.atMostOne</a>,
 <a href="../reference/libtmux-fsharp-query.md#matching">Query.matching</a>,
 <a href="../reference/libtmux-fsharp-selection.md#exactlyOne">Selection.exactlyOne</a>,
 <a href="../reference/libtmux-fsharp-session.md#windows">Session.windows</a>,
 <a href="../reference/libtmux-fsharp-session.md#panes">Session.panes</a>,
 <a href="../reference/libtmux-fsharp-window.md#panes">Window.panes</a>,
 <a href="../reference/libtmux-fsharp-session.md#activePane">Session.activePane</a>,
 <a href="../reference/libtmux-fsharp-window.md#activePane">Window.activePane</a></p><p class='fsdocs-para'><b>Changing things.</b> Creating a session from a
 <a href="../reference/libtmux-fsharp-sessionspec.md">SessionSpec</a> includes its windows and their
 splits, made one step after another, so a failure part way leaves what was
 already created. A name already taken throws
 TmuxSessionExistsException. Everything after that is
 a function on the value it changes, in the Session, Window, and Pane modules,
 and the Chain module runs several commands in one tmux invocation.
 </p><p class='fsdocs-para'><a href="../reference/libtmux-fsharp-server.md#newSession">Server.newSession</a>,
 <a href="../reference/libtmux-fsharp-session.md">Session</a>,
 <a href="../reference/libtmux-fsharp-window.md">Window</a>,
 <a href="../reference/libtmux-fsharp-pane.md">Pane</a>,
 <a href="../reference/libtmux-fsharp-chain.md">Chain</a></p><p class='fsdocs-para'><b>Options and hooks.</b> This module has none.
 <a href="../reference/libtmux-fsharp-options.md">Options</a> reads and writes an option through a
 typed key, given the options of the scope the option belongs to, such as the
 core handle&#39;s <code>Options</code> for a server option. Its global hooks are the
 handle&#39;s <code>Hooks</code>.
 </p><p class='fsdocs-para'><a href="../reference/libtmux-fsharp-options.md#get">Options.get</a>,
 <a href="../reference/libtmux-fsharp-options.md#set">Options.set</a></p><p class='fsdocs-para'>
 Paste buffers, key bindings, configuration, shell commands, wait-for
 channels, and the message log are members of the core <code>LibTmux.Server</code>,
 which F# calls like any .NET type, and so are menus, command prompts,
 confirmations, and the commands that attach, switch, detach, lock, or suspend
 a client. All but attach act on an attached client, and tmux refuses them
 when there is none; attaching makes the client instead, so it needs a
 terminal of its own. Anything tmux can do that is not here is reachable
 through the core handle&#39;s <code>ExecuteCommandAsync</code>, which runs one raw
 command and hands back its result, or through
 <a href="../reference/libtmux-fsharp-chain.md">Chain</a>, which runs several in one tmux
 invocation.
 </p>

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L139)

<a name="clients"></a>

#### <code><span>Server.clients&#32;<span>server</span></span></code>

Queries attached clients.

tmux narrows a filtered client listing only from tmux 3.4; older tmux lists every client.

**Parameters:**

**server**: <code>Server</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Client&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L27)

<a name="connect"></a>

#### <code><span>Server.connect&#32;<span>cancellationToken&#32;options</span></span></code>

Attaches to a server already listening on the socket the options name.

The core&#39;s <code>Server.ConnectAsync</code>; it never starts a server.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**options**: <code>ServerConnectionOptions</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Server&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L15)

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L12)

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

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The session and its first window name different directories, the
 first window sets an environment, or a split&#39;s size is out of range.

`TmuxSessionExistsException` The name is already taken.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L62)

<a name="panes"></a>

#### <code><span>Server.panes&#32;<span>server</span></span></code>

Queries every pane.

**Parameters:**

**server**: <code>Server</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Pane&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L24)

<a name="sessions"></a>

#### <code><span>Server.sessions&#32;<span>server</span></span></code>

Queries every session.

Child windows and panes require an explicit capture at the corresponding depth.

**Parameters:**

**server**: <code>Server</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Session&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L18)

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L160)

<a name="tryFindPane"></a>

#### <code><span>Server.tryFindPane&#32;<span>cancellationToken&#32;id&#32;server</span></span></code>

Returns a pane or None after a successful lookup establishes absence.

Connection, command and cancellation errors propagate unchanged.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**id**: <code>PaneId</code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>Pane&#32;option</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L154)

<a name="tryFindSession"></a>

#### <code><span>Server.tryFindSession&#32;<span>cancellationToken&#32;id&#32;server</span></span></code>

Returns a session or None after a successful lookup establishes absence.

Connection, command and cancellation errors propagate unchanged.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**id**: <code>SessionId</code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>Session&#32;option</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L142)

<a name="tryFindWindow"></a>

#### <code><span>Server.tryFindWindow&#32;<span>cancellationToken&#32;id&#32;server</span></span></code>

Returns a window or None after a successful lookup establishes absence.

Connection, command and cancellation errors propagate unchanged.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**id**: <code>WindowId</code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>Window&#32;option</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L148)

<a name="windows"></a>

#### <code><span>Server.windows&#32;<span>server</span></span></code>

Queries window placements across all sessions.

A linked window appears once for each session it is linked into.

**Parameters:**

**server**: <code>Server</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Window&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L21)

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L30)
