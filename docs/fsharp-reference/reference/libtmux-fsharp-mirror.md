## Mirror module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Follows a server&#39;s sessions, windows, panes and clients as tmux announces changes.

Each announcement starts a fresh capture; a capture that finds nothing
 different publishes nothing. tmux does not announce a pane&#39;s running command
 or working directory, nor layout changes in other sessions; use
 <code>startRefreshing</code> to see those within an interval.

### Functions and values

<a name="current"></a>

#### <code><span>Mirror.current&#32;<span>mirror</span></span></code>

Returns the latest published view.

**Parameters:**

**mirror**: <code>ServerMirror</code>

Returns: <code>ServerMirrorView</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L144)

<a name="start"></a>

#### <code><span>Mirror.start&#32;<span>cancellationToken&#32;anchor</span></span></code>

Mirrors the server an anchor session belongs to, capturing on each announcement.

The caller owns and asynchronously disposes the returned mirror.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**anchor**: <code>Session</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;ServerMirror&gt;</span></code>

`IncompleteSnapshotException` The session was not read through a server.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L138)

<a name="startRefreshing"></a>

#### <code><span>Mirror.startRefreshing&#32;<span>cancellationToken&#32;every&#32;anchor</span></span></code>

Mirrors a server, also capturing whenever it has been quiet for an interval.

The caller owns and asynchronously disposes the returned mirror.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**every**: <code><a href="https://learn.microsoft.com/dotnet/api/system.timespan">TimeSpan</a></code>

**anchor**: <code>Session</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;ServerMirror&gt;</span></code>

[ArgumentOutOfRangeException](https://learn.microsoft.com/dotnet/api/system.argumentoutofrangeexception) The interval is negative.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L141)

<a name="views"></a>

#### <code><span>Mirror.views&#32;<span>mirror</span></span></code>

Streams the current view and each newer one, skipping views published while the reader was busy.

The stream is cold, ends when the mirror ends, and raises the failure that ended it.

**Parameters:**

**mirror**: <code>ServerMirror</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.iasyncenumerable-1">IAsyncEnumerable</a>&lt;ServerMirrorView&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L146)

<a name="waitUntil"></a>

#### <code><span>Mirror.waitUntil&#32;<span>cancellationToken&#32;timeout&#32;condition&#32;mirror</span></span></code>

Waits until a view satisfies a condition, testing the current view first.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**timeout**: <code><a href="https://learn.microsoft.com/dotnet/api/system.timespan">TimeSpan</a></code>

**condition**: <code><span>ServerMirrorView&#32;->&#32;bool</span></code>

**mirror**: <code>ServerMirror</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;ServerMirrorView&gt;</span></code>

`TmuxWaitTimeoutException` No view satisfied the condition in time.

[InvalidOperationException](https://learn.microsoft.com/dotnet/api/system.invalidoperationexception) The mirror ended first.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L148)
