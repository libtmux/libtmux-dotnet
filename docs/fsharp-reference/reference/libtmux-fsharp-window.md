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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L185)

<a name="panes"></a>

#### <code><span>Window.panes&#32;<span>window</span></span></code>

Queries the panes in a window.

**Parameters:**

**window**: <code>Window</code>

Returns: <code><span><a href="../reference/libtmux-fsharp-query-1.md">Query</a>&lt;Pane&gt;</span></code>

`IncompleteSnapshotException` The window was not read through a server.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L182)

<a name="placementKey"></a>

#### <code><span>Window.placementKey&#32;<span>window</span></span></code>

Returns a comparable key including the captured session and window index.

**Parameters:**

**window**: <code>Window</code>

Returns: <code><a href="../reference/libtmux-fsharp-windowplacementkey.md">WindowPlacementKey</a></code>

`IncompleteSnapshotException` The placement was not captured.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L180)
