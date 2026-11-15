## Window module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Identifies window placements and starts queries confined to one window.

<p class='fsdocs-para'>
 Each function takes the window it acts on last. One that runs a tmux command
 also takes the cancellation token first and returns a <code>Task</code>. The
 window is the core <code>LibTmux.Window</code> handle, which is immutable and safe
 to share, and a function that changes it returns a handle carrying the state
 afterwards.
 </p><p class='fsdocs-para'><b>What is on here</b></p><p class='fsdocs-para'>
 A tmux window has a good many members, but this module covers the part an F#
 caller reaches first. The rest lives on the core handle. The members fall
 into a few groups:
 </p><p class='fsdocs-para'><b>Looking inside.</b> Start a query over the window&#39;s panes, which throws
 IncompleteSnapshotException when the window was not
 read through a server. Or ask tmux now for the window&#39;s active pane, which
 throws TmuxObjectNotFoundException when tmux reports
 none. A linked window is one placement per session, and the placement key
 names the session and index a handle was captured at.
 </p><p class='fsdocs-para'><a href="../reference/libtmux-fsharp-window.md#panes">Window.panes</a>,
 <a href="../reference/libtmux-fsharp-window.md#activePane">Window.activePane</a>,
 <a href="../reference/libtmux-fsharp-window.md#placementKey">Window.placementKey</a></p><p class='fsdocs-para'><b>Arranging.</b> A layout is applied by name, and a name tmux may not
 recognise is refused with TmuxWindowException before
 anything is sent. Resizing and moving take the core request.
 </p><p class='fsdocs-para'><a href="../reference/libtmux-fsharp-window.md#selectLayout">Window.selectLayout</a>,
 <a href="../reference/libtmux-fsharp-window.md#resize">Window.resize</a>,
 <a href="../reference/libtmux-fsharp-window.md#move">Window.move</a></p><p class='fsdocs-para'><b>Changing the window.</b> Renaming returns a handle carrying the new name,
 and tmux expands the name as a format, so a <code>#</code> in it does not survive
 verbatim. Selecting makes the window its session&#39;s current one. Killing the
 window takes its panes with it, and leaves off the core&#39;s other kill options.
 </p><p class='fsdocs-para'><a href="../reference/libtmux-fsharp-window.md#rename">Window.rename</a>,
 <a href="../reference/libtmux-fsharp-window.md#select">Window.select</a>,
 <a href="../reference/libtmux-fsharp-window.md#kill">Window.kill</a></p><p class='fsdocs-para'>
 The rest of a window is on the core <code>LibTmux.Window</code>, which F# calls like
 any .NET type: creating panes, linking and unlinking, rotating, its options
 and hooks, and the raw command through <code>ExecuteCommandAsync</code>.
 </p>

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L215)

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L212)

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

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The layout name is blank.

`TmuxWindowException`tmux may not recognise the layout, so it is refused before anything is sent.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L208)
