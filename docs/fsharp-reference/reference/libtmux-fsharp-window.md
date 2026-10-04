## Window module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Identifies window placements and starts queries confined to one window.

### Functions and values

<a name="activePane"></a>

#### <code><span>Window.activePane&#32;<span>cancellationToken&#32;window</span></span></code>

Reads from tmux the window&#39;s active pane.

The core&#39;s <code>Window.GetActivePaneAsync</code>.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**window**: <code>Window</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Pane&gt;</span></code>

`TmuxObjectNotFoundException`tmux reports no such pane.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L197)

<a name="kill"></a>

#### <code><span>Window.kill&#32;<span>cancellationToken&#32;window</span></span></code>

Kills the window, with its panes.

The core&#39;s <code>Window.KillAsync</code> without <code>allExcept</code>.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**window**: <code>Window</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L205)

<a name="move"></a>

#### <code><span>Window.move&#32;<span>cancellationToken&#32;request&#32;window</span></span></code>

Moves the window as the request says, and returns a handle carrying the state afterwards.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>MoveWindowRequest</code>

**window**: <code>Window</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Window&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L214)

<a name="panes"></a>

#### <code><span>Window.panes&#32;<span>window</span></span></code>

Queries the panes in a window.

**Parameters:**

**window**: <code>Window</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Pane&gt;</span></code>

`IncompleteSnapshotException` The window was not read through a server.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L194)

<a name="placementKey"></a>

#### <code><span>Window.placementKey&#32;<span>window</span></span></code>

Returns a comparable key including the captured session and window index.

**Parameters:**

**window**: <code>Window</code>

Returns: <code><a href="../reference/libtmux-fsharp-windowplacementkey.md">WindowPlacementKey</a></code>

`IncompleteSnapshotException` The placement was not captured.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L192)

<a name="rename"></a>

#### <code><span>Window.rename&#32;<span>cancellationToken&#32;name&#32;window</span></span></code>

Renames the window and returns a handle carrying the new name.

tmux expands the name as a format, so a <code>#</code> in it does not survive verbatim.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**name**: <code>string</code>

**window**: <code>Window</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Window&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L200)

<a name="resize"></a>

#### <code><span>Window.resize&#32;<span>cancellationToken&#32;request&#32;window</span></span></code>

Resizes the window as the request says, and returns a handle carrying the state afterwards.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>ResizeWindowRequest</code>

**window**: <code>Window</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Window&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L211)

<a name="select"></a>

#### <code><span>Window.select&#32;<span>cancellationToken&#32;window</span></span></code>

Makes the window its session's current window, and returns a handle carrying the state afterwards.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**window**: <code>Window</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Window&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L203)

<a name="selectLayout"></a>

#### <code><span>Window.selectLayout&#32;<span>cancellationToken&#32;layout&#32;window</span></span></code>

Arranges the window&#39;s panes in a layout, such as <code>even-horizontal</code> or <code>tiled</code>, and returns a handle carrying the state afterwards.

The core&#39;s <code>Window.SelectLayoutAsync</code> with a named layout; pass a request to it to cycle layouts instead.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**layout**: <code>string</code>

**window**: <code>Window</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Window&gt;</span></code>

`TmuxWindowException`tmux may not recognise the layout, so it is refused before anything is sent.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L208)
