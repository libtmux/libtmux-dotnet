## Server module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Starts explicit server reads with the caller's cancellation token.

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L73)

<a name="listPanes"></a>

#### <code><span>Server.listPanes&#32;<span>cancellationToken&#32;server</span></span></code>

Lists panes and captures their scalar fields.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1">IReadOnlyList</a>&lt;Pane&gt;</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L70)

<a name="tryFindPane"></a>

#### <code><span>Server.tryFindPane&#32;<span>cancellationToken&#32;id&#32;server</span></span></code>

Returns a pane or None after a successful lookup establishes absence.

Connection, command and cancellation errors propagate unchanged.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**id**: <code>PaneId</code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>Pane&#32;option</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L76)
