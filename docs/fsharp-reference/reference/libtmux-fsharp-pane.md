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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L193)

<a name="currentCommand"></a>

#### <code><span>Pane.currentCommand&#32;<span>pane</span></span></code>

Reads the captured command name, preserving an empty string.

**Parameters:**

**pane**: <code>Pane</code>

Returns: <code><span>string&#32;option</span></code>

`IncompleteSnapshotException` The command field was not captured.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L191)

<a name="currentPath"></a>

#### <code><span>Pane.currentPath&#32;<span>pane</span></span></code>

Reads the captured working directory, preserving an empty string.

**Parameters:**

**pane**: <code>Pane</code>

Returns: <code><span>string&#32;option</span></code>

`IncompleteSnapshotException` The path field was not captured.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L190)

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L196)

<a name="pressKey"></a>

#### <code><span>Pane.pressKey&#32;<span>cancellationToken&#32;key&#32;pane</span></span></code>

Presses one key by its tmux name, such as <code>Enter</code>, <code>C-c</code> or <code>Up</code>.

tmux types a name it does not know as text. Cancellation can occur after dispatch; it does not undo the key.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**key**: <code>string</code>

**pane**: <code>Pane</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The key is empty or white space.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L242)

<a name="run"></a>

#### <code><span>Pane.run&#32;<span>cancellationToken&#32;timeout&#32;command&#32;pane</span></span></code>

Runs a shell command in the pane and waits for its exit status and output.

The pane must sit at a POSIX shell prompt. A command still running at
 the timeout keeps running; the result reports <code>TimedOut</code>.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**timeout**: <code><a href="https://learn.microsoft.com/dotnet/api/system.timespan">TimeSpan</a></code>

**command**: <code>string</code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;PaneRunResult&gt;</span></code>

`TmuxPaneException` The pane is in a mode or not running a POSIX shell.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L233)

<a name="sendAndWait"></a>

#### <code><span>Pane.sendAndWait&#32;<span>cancellationToken&#32;timeout&#32;line&#32;text&#32;pane</span></span></code>

Types a line, presses Enter, and waits for a later line to contain the text.

The screen before the line is typed never ends the wait, and the
 shell&#39;s echo of the line is discounted: waiting for <code>done</code> after
 typing <code>echo done</code> waits for the command&#39;s output. Prefer this to
 <code>sendKeys</code> followed by <code>waitForText</code>, which can match the
 typed line itself.
 Running out of time returns the outcome <code>TimedOut</code>; only <code>Mirror.waitUntil</code> raises instead.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**timeout**: <code><a href="https://learn.microsoft.com/dotnet/api/system.timespan">TimeSpan</a></code>

**line**: <code>string</code>

**text**: <code>string</code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;PaneWaitResult&gt;</span></code>

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The text is empty or spans lines.

`TmuxPaneException` The pane&#39;s program had already exited.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L208)

<a name="sendAndWaitFor"></a>

#### <code><span>Pane.sendAndWaitFor&#32;<span>cancellationToken&#32;keys&#32;request&#32;pane</span></span></code>

Sends keys as the request describes, then waits as the wait request describes.

As <code>sendAndWait</code>: only output after the keys counts, and literal
 text is discounted from it. Key names are not.
 Running out of time returns the outcome <code>TimedOut</code>; only <code>Mirror.waitUntil</code> raises instead.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**keys**: <code>SendKeysRequest</code>

**request**: <code>PaneWaitRequest</code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;PaneWaitResult&gt;</span></code>

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The wait names no pattern.

`TmuxPaneException` The pane&#39;s program had already exited.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L217)

<a name="sendKeys"></a>

#### <code><span>Pane.sendKeys&#32;<span>cancellationToken&#32;request&#32;pane</span></span></code>

Sends text or key names according to the request&#39;s literal and Enter settings.

Cancellation can occur after dispatch; it does not undo sent keys.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>SendKeysRequest</code>

**pane**: <code>Pane</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L246)

<a name="sendLine"></a>

#### <code><span>Pane.sendLine&#32;<span>cancellationToken&#32;line&#32;pane</span></span></code>

Types a line into the pane as literal text, then presses Enter.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**line**: <code>string</code>

**pane**: <code>Pane</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The line contains NUL.

`LibTmuxException` The text was sent but Enter failed; whether tmux pressed it is unknown, so do not send the line again.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L236)

<a name="sendText"></a>

#### <code><span>Pane.sendText&#32;<span>cancellationToken&#32;text&#32;pane</span></span></code>

Types text into the pane literally, without pressing Enter.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**text**: <code>string</code>

**pane**: <code>Pane</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The text contains NUL.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L239)

<a name="split"></a>

#### <code><span>Pane.split&#32;<span>cancellationToken&#32;request&#32;pane</span></span></code>

Splits the pane and returns the new pane handle.

It takes the core request, which carries every split-window option;
 <code>SplitSpec</code> describes only the splits a <code>Server.newSession</code>
 spec builds. Cancellation can leave the split applied; do not retry
 automatically.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>SplitPaneRequest</code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Pane&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L249)

<a name="waitFor"></a>

#### <code><span>Pane.waitFor&#32;<span>cancellationToken&#32;request&#32;pane</span></span></code>

Waits as the request describes: patterns, stop patterns, or any output.

Running out of time returns the outcome <code>TimedOut</code>; only <code>Mirror.waitUntil</code> raises instead.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>PaneWaitRequest</code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;PaneWaitResult&gt;</span></code>

`TmuxPaneException` The pane&#39;s program had already exited.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L205)

<a name="waitForText"></a>

#### <code><span>Pane.waitForText&#32;<span>cancellationToken&#32;timeout&#32;text&#32;pane</span></span></code>

Waits for a line the pane prints to contain the text.

Text already on screen ends the wait at once as <code>PresentAtEntry</code>.
 The wait sleeps on the pane&#39;s own output rather than polling, and ends
 early when the pane&#39;s program exits or a full-screen program starts.
 Running out of time returns the outcome <code>TimedOut</code>; only <code>Mirror.waitUntil</code> raises instead.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**timeout**: <code><a href="https://learn.microsoft.com/dotnet/api/system.timespan">TimeSpan</a></code>

**text**: <code>string</code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;PaneWaitResult&gt;</span></code>

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The text is empty or spans lines.

`TmuxPaneException` The pane&#39;s program had already exited.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L202)

<a name="waitUntil"></a>

#### <code><span>Pane.waitUntil&#32;<span>cancellationToken&#32;timeout&#32;condition&#32;pane</span></span></code>

Waits until a condition holds over the rows the pane shows, top to bottom.

The condition sees the whole screen each time the pane prints or changes state.
 Running out of time returns the outcome <code>TimedOut</code>; only <code>Mirror.waitUntil</code> raises instead.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**timeout**: <code><a href="https://learn.microsoft.com/dotnet/api/system.timespan">TimeSpan</a></code>

**condition**: <code><span><span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1">IReadOnlyList</a>&lt;string&gt;</span>&#32;->&#32;bool</span></code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;PaneWaitResult&gt;</span></code>

`TmuxPaneException` The pane&#39;s program had already exited.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L225)
