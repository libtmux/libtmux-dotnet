## Session module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Starts queries confined to one session.

<p class='fsdocs-para'>
 Each function takes the session it acts on last. One that runs a tmux command
 also takes the cancellation token first and returns a <code>Task</code>. The
 session is the core <code>LibTmux.Session</code> handle, which is immutable and
 safe to share.
 </p><p class='fsdocs-para'><b>What is on here</b></p><p class='fsdocs-para'>
 A tmux session has a good many members, but this module covers the part an
 F# caller reaches first. The rest lives on the core handle. The members fall
 into a few groups:
 </p><p class='fsdocs-para'><b>Looking inside.</b> Start a query over the session&#39;s windows or panes;
 either throws IncompleteSnapshotException when the
 session was not read through a server. Or ask tmux now for the pane the
 session shows, its current window&#39;s active pane, which throws
 TmuxObjectNotFoundException when tmux reports none.
 </p><p class='fsdocs-para'><a href="../reference/libtmux-fsharp-session.md#windows">Session.windows</a>,
 <a href="../reference/libtmux-fsharp-session.md#panes">Session.panes</a>,
 <a href="../reference/libtmux-fsharp-session.md#activePane">Session.activePane</a></p><p class='fsdocs-para'><b>Changing the session.</b> Renaming returns a handle carrying the new name,
 and tmux expands the name as a format, so a <code>#</code> in it does not survive
 verbatim. Killing the session takes its windows and panes with it, and leaves
 off the core&#39;s other kill options. Creating a window returns it as the
 request describes, and cancellation can leave the window created.
 </p><p class='fsdocs-para'><a href="../reference/libtmux-fsharp-session.md#rename">Session.rename</a>,
 <a href="../reference/libtmux-fsharp-session.md#kill">Session.kill</a>,
 <a href="../reference/libtmux-fsharp-session.md#newWindow">Session.newWindow</a></p><p class='fsdocs-para'><b>Waiting.</b> Each wait on the session&#39;s panes attaches a control client
 and lets it go when the wait ends. Holding one across a series of waits saves
 that attach for each, until the handle is disposed; use it with <code>use!</code>.
 </p><p class='fsdocs-para'><a href="../reference/libtmux-fsharp-session.md#holdWaitClient">Session.holdWaitClient</a></p><p class='fsdocs-para'>
 The rest of a session is on the core <code>LibTmux.Session</code>, which F# calls
 like any .NET type: selecting and stopping windows, locking, attaching and
 detaching clients, and the session&#39;s options, hooks, and environment.
 <a href="../reference/libtmux-fsharp-options.md">Options</a> reads and writes an option through the
 options of that scope.
 </p>

### Functions and values

<a name="activePane"></a>

#### <code><span>Session.activePane&#32;<span>cancellationToken&#32;session</span></span></code>

Reads from tmux the pane the session shows: its current window&#39;s active pane.

The core&#39;s <code>Session.GetActivePaneAsync</code>; a new session&#39;s only pane is this one.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**session**: <code>Session</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Pane&gt;</span></code>

`TmuxObjectNotFoundException`tmux reports no such pane.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L175)

<a name="holdWaitClient"></a>

#### <code><span>Session.holdWaitClient&#32;<span>cancellationToken&#32;session</span></span></code>

Keeps the control client that waits on the session&#39;s panes use attached until the handle is disposed.

The core&#39;s <code>Session.HoldWaitClientAsync</code>. Each wait attaches a client and lets it go when it ends;
 holding one across a series of waits saves that attach for each. Use it with <code>use!</code>.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**session**: <code>Session</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<a href="https://learn.microsoft.com/dotnet/api/system.iasyncdisposable">IAsyncDisposable</a>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L178)

<a name="kill"></a>

#### <code><span>Session.kill&#32;<span>cancellationToken&#32;session</span></span></code>

Kills the session, with its windows and panes.

The core&#39;s <code>Session.KillAsync</code> with its other options left off.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**session**: <code>Session</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L184)

<a name="newWindow"></a>

#### <code><span>Session.newWindow&#32;<span>cancellationToken&#32;request&#32;session</span></span></code>

Creates a window in the session as the request describes, and returns it.

The core&#39;s <code>Session.CreateWindowAsync</code>; cancellation can leave the window created.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>NewWindowRequest</code>

**session**: <code>Session</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Window&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L187)

<a name="panes"></a>

#### <code><span>Session.panes&#32;<span>session</span></span></code>

Queries the panes of every window in a session.

**Parameters:**

**session**: <code>Session</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Pane&gt;</span></code>

`IncompleteSnapshotException` The session was not read through a server.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L172)

<a name="rename"></a>

#### <code><span>Session.rename&#32;<span>cancellationToken&#32;name&#32;session</span></span></code>

Renames the session and returns a handle carrying the new name.

tmux expands the name as a format, so a <code>#</code> in it does not survive verbatim.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**name**: <code>string</code>

**session**: <code>Session</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Session&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L181)

<a name="windows"></a>

#### <code><span>Session.windows&#32;<span>session</span></span></code>

Queries the window placements in a session.

**Parameters:**

**session**: <code>Session</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Window&gt;</span></code>

`IncompleteSnapshotException` The session was not read through a server.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L169)
