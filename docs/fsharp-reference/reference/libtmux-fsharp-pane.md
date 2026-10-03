## Pane module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Reads captured pane fields and starts explicit pane operations.

### Functions and values

<a name="capture"></a>

#### <code><span>Pane.capture&#32;<span>cancellationToken&#32;request&#32;pane</span></span></code>

Captures pane contents using the supplied core request.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>CapturePaneRequest</code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1">IReadOnlyList</a>&lt;string&gt;</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L70)

<a name="currentCommand"></a>

#### <code><span>Pane.currentCommand&#32;<span>pane</span></span></code>

Reads the captured command name, preserving an empty string.

**Parameters:**

**pane**: <code>Pane</code>

Returns: <code><span>string&#32;option</span></code>

`IncompleteSnapshotException` The command field was not captured.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L68)

<a name="currentPath"></a>

#### <code><span>Pane.currentPath&#32;<span>pane</span></span></code>

Reads the captured working directory, preserving an empty string.

**Parameters:**

**pane**: <code>Pane</code>

Returns: <code><span>string&#32;option</span></code>

`IncompleteSnapshotException` The path field was not captured.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L67)

<a name="findOnScreen"></a>

#### <code><span>Pane.findOnScreen&#32;<span>cancellationToken&#32;search&#32;pane</span></span></code>

Returns the first visible row showing the text, counted from 1, or None.

tmux searches only the rows on screen. Capture the history and filter its lines to search further back.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**search**: <code><a href="../reference/libtmux-fsharp-screensearch.md">ScreenSearch</a></code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span>int&#32;option</span>&gt;</span></code>

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The text cannot be written as a tmux format.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L73)

<a name="sendKeys"></a>

#### <code><span>Pane.sendKeys&#32;<span>cancellationToken&#32;request&#32;pane</span></span></code>

Sends text or key names according to the request&#39;s literal and Enter settings.

Cancellation can occur after dispatch; it does not undo sent keys.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>SendKeysRequest</code>

**pane**: <code>Pane</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L79)

<a name="split"></a>

#### <code><span>Pane.split&#32;<span>cancellationToken&#32;request&#32;pane</span></span></code>

Splits the pane and returns the new pane handle.

Cancellation can leave the split applied; do not retry automatically.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>SplitPaneRequest</code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Pane&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L82)
