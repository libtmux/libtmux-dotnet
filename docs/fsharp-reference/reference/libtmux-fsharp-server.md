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
 </p><p class='fsdocs-para'><b>Connecting. </b><a href="../reference/libtmux-fsharp-server.md#connect">Server.connect</a> finds a
 server already listening on the socket the options name and never starts
 one, while <a href="../reference/libtmux-fsharp-server.md#createOwned">Server.createOwned</a> starts one
 there and owns it, stopping it when the scope is disposed; it refuses a
 server already listening on the default socket.
 <a href="../reference/libtmux-fsharp-server.md#within">Server.within</a> bounds every command by a
 timeout, and a command that outlasts it fails as <code>MayHaveRun</code>.
 <a href="../reference/libtmux-fsharp-control.md#enter">Control.enter</a> in
 <a href="../reference/libtmux-fsharp-control.md">Control</a> opens a control client that reports
 what tmux does.
 </p><p class='fsdocs-para'><b>Finding one thing. </b><a href="../reference/libtmux-fsharp-server.md#tryFindSession">Server.tryFindSession</a>,
 <a href="../reference/libtmux-fsharp-server.md#tryFindWindow">Server.tryFindWindow</a>, and
 <a href="../reference/libtmux-fsharp-server.md#tryFindPane">Server.tryFindPane</a> take an id, and
 <a href="../reference/libtmux-fsharp-server.md#tryFindClient">Server.tryFindClient</a> takes an exact name.
 Each returns <code>None</code> after a successful lookup establishes absence;
 connection, command and cancellation errors propagate unchanged.
 </p><p class='fsdocs-para'><b>Listing everything. </b><a href="../reference/libtmux-fsharp-server.md#sessions">Server.sessions</a>,
 <a href="../reference/libtmux-fsharp-server.md#windows">Server.windows</a>,
 <a href="../reference/libtmux-fsharp-server.md#panes">Server.panes</a>, and
 <a href="../reference/libtmux-fsharp-server.md#clients">Server.clients</a> each describe a listing as a
 <a href="../reference/libtmux-fsharp-query-1.md">Query</a> and read nothing until it is run;
 every run reads tmux again. <a href="../reference/libtmux-fsharp-server.md#capture">Server.capture</a>
 reads the tree to the depth asked for and returns a new handle whose
 captured relations answer without another command, and
 <a href="../reference/libtmux-fsharp-snapshot.md#relation">Snapshot.relation</a> says whether one was read.
 </p><p class='fsdocs-para'><b>Querying things and getting around. </b><a href="../reference/libtmux-fsharp-query.md">Query</a> narrows a listing:
 <a href="../reference/libtmux-fsharp-query.md#where">Query.where</a> adds a
 <a href="../reference/libtmux-fsharp-filter-1.md">Filter</a> built from
 <a href="../reference/libtmux-fsharp-sessionfields.md">SessionFields</a>,
 <a href="../reference/libtmux-fsharp-windowfields.md">WindowFields</a>,
 <a href="../reference/libtmux-fsharp-panefields.md">PaneFields</a>, or
 <a href="../reference/libtmux-fsharp-clientfields.md">ClientFields</a>;
 <a href="../reference/libtmux-fsharp-query.md#whereUnsafe">Query.whereUnsafe</a> hands tmux a raw filter,
 and a malformed one keeps no rows rather than failing; and
 <a href="../reference/libtmux-fsharp-query.md#showing">Query.showing</a> keeps the panes whose visible
 rows show some text. <a href="../reference/libtmux-fsharp-query.md#list">Query.list</a> runs the
 query, and <a href="../reference/libtmux-fsharp-query.md#exactlyOne">Query.exactlyOne</a>,
 <a href="../reference/libtmux-fsharp-query.md#tryExactlyOne">Query.tryExactlyOne</a>, and
 <a href="../reference/libtmux-fsharp-query.md#atMostOne">Query.atMostOne</a> read a single match.
 <a href="../reference/libtmux-fsharp-query.md#matching">Query.matching</a> filters objects already read,
 and <a href="../reference/libtmux-fsharp-selection.md">Selection</a>&#39;s
 <a href="../reference/libtmux-fsharp-selection.md#exactlyOne">Selection.exactlyOne</a> picks the sole element
 of any sequence. To move between levels,
 <a href="../reference/libtmux-fsharp-session.md#windows">Session.windows</a> and
 <a href="../reference/libtmux-fsharp-session.md#panes">Session.panes</a> start a query inside a
 session, <a href="../reference/libtmux-fsharp-window.md#panes">Window.panes</a> starts one inside a
 window, and a session&#39;s <a href="../reference/libtmux-fsharp-session.md#activePane">Session.activePane</a>
 and a window&#39;s <a href="../reference/libtmux-fsharp-window.md#activePane">Window.activePane</a> read the
 focused pane from tmux.
 </p><p class='fsdocs-para'><b>Changing things. </b><a href="../reference/libtmux-fsharp-server.md#newSession">Server.newSession</a>
 creates a session from a <a href="../reference/libtmux-fsharp-sessionspec.md">SessionSpec</a>, its
 windows and their splits included, one step after another, so a failure part
 way leaves what was already created. Everything after that is a function on
 the value it changes, in <a href="../reference/libtmux-fsharp-session.md">Session</a>,
 <a href="../reference/libtmux-fsharp-window.md">Window</a>, and
 <a href="../reference/libtmux-fsharp-pane.md">Pane</a>, and
 <a href="../reference/libtmux-fsharp-chain.md">Chain</a> runs several commands in one tmux
 invocation.
 </p><p class='fsdocs-para'><b>Options and hooks. </b>This module has none.
 <a href="../reference/libtmux-fsharp-options.md">Options</a> reads and writes an option through a
 typed key with <a href="../reference/libtmux-fsharp-options.md#get">Options.get</a> and
 <a href="../reference/libtmux-fsharp-options.md#set">Options.set</a>, given the options of the scope
 the option belongs to, such as the core handle&#39;s <code>Options</code> for a server
 option. Its global hooks are the handle&#39;s <code>Hooks</code>.
 </p><p class='fsdocs-para'><b>Everything else tmux keeps. </b>Paste buffers, key bindings,
 configuration, shell commands, wait-for channels, and the message log are
 members of the core <code>LibTmux.Server</code>, which F# calls like any .NET
 type.
 </p><p class='fsdocs-para'><b>Things that need a terminal. </b>Menus, command prompts, confirmations,
 and the commands that attach, switch, detach, lock, or suspend a client are
 members of the core <code>LibTmux.Server</code> too. All but attach act on an
 attached client, and tmux refuses them when there is none. Attaching makes
 the client instead, so it needs a terminal of its own.
 </p><p class='fsdocs-para'>
 Anything tmux can do that is not here is reachable through the core handle&#39;s
 <code>ExecuteCommandAsync</code>, which runs one raw command and hands back its
 result, or through <a href="../reference/libtmux-fsharp-chain.md">Chain</a>, which runs several
 in one tmux invocation.
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
