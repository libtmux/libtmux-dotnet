## Session module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Starts queries confined to one session.

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
