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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L250)

<a name="clearHistory"></a>

#### <code><span>Pane.clearHistory&#32;<span>cancellationToken&#32;pane</span></span></code>

Clears the pane's scrollback history; what the screen shows stays.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**pane**: <code>Pane</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L330)

<a name="currentCommand"></a>

#### <code><span>Pane.currentCommand&#32;<span>pane</span></span></code>

Reads the captured command name, preserving an empty string.

**Parameters:**

**pane**: <code>Pane</code>

Returns: <code><span>string&#32;option</span></code>

`IncompleteSnapshotException` The command field was not captured.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L248)

<a name="currentPath"></a>

#### <code><span>Pane.currentPath&#32;<span>pane</span></span></code>

Reads the captured working directory, preserving an empty string.

**Parameters:**

**pane**: <code>Pane</code>

Returns: <code><span>string&#32;option</span></code>

`IncompleteSnapshotException` The path field was not captured.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L247)

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L256)

<a name="kill"></a>

#### <code><span>Pane.kill&#32;<span>cancellationToken&#32;pane</span></span></code>

Kills the pane and the program in it.

The core&#39;s <code>Pane.KillAsync</code> without <code>allExcept</code>.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**pane**: <code>Pane</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L315)

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L302)

<a name="readSince"></a>

#### <code><span>Pane.readSince&#32;<span>cancellationToken&#32;position&#32;pane</span></span></code>

Reads what the pane printed since a position, and where this read finished.

The core&#39;s <code>Pane.ReadOutputSinceAsync</code>. Start with <code>None</code>, which returns no lines and a
 position; pass each result&#39;s <code>Position</code> to the next read. <code>LinesMissed</code> says scrollback
 dropped output first. This is the MCP server&#39;s <code>capture_since</code>.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**position**: <code><span>PaneOutputPosition&#32;option</span></code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;PaneOutputSince&gt;</span></code>

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The position came from another pane.

`TmuxPaneException` The pane runs a different program than when the position was taken, or a read without a position found its program exited.

`TmuxObjectNotFoundException`tmux no longer has the pane.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L253)

<a name="resize"></a>

#### <code><span>Pane.resize&#32;<span>cancellationToken&#32;request&#32;pane</span></span></code>

Resizes the pane as the request says, and returns a handle carrying the state afterwards.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>ResizePaneRequest</code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Pane&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L321)

<a name="respawn"></a>

#### <code><span>Pane.respawn&#32;<span>cancellationToken&#32;request&#32;pane</span></span></code>

Starts the pane&#39;s program again as the request says.

The core&#39;s <code>Pane.RespawnAsync</code>. tmux refuses a pane whose program is still running unless
 <code>KillExistingProcess</code> is set, which kills that program first.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>RespawnRequest</code>

**pane**: <code>Pane</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L327)

<a name="run"></a>

#### <code><span>Pane.run&#32;<span>cancellationToken&#32;timeout&#32;command&#32;pane</span></span></code>

Runs a shell command in the pane and waits for its exit status and output.

The pane must sit at a POSIX shell prompt. A command still running at
 the timeout keeps running; the result reports <code>TimedOut</code>. A command
 that prints more than scrollback holds reports <code>LinesMissed</code>, and its
 <code>Output</code> is then what the pane still showed.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**timeout**: <code><a href="https://learn.microsoft.com/dotnet/api/system.timespan">TimeSpan</a></code>

**command**: <code>string</code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;PaneRunResult&gt;</span></code>

`TmuxPaneException` The pane is in a mode, not running a POSIX shell, or its program has exited; or it changed during every read before the command was sent.

`TmuxObjectNotFoundException`tmux no longer has the pane.

`LibTmuxException` The command was sent and the run was cancelled or could not be observed; <code>TmuxFailure.MayHaveRun</code> matches it, and the pane needs inspecting before a retry.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L293)

<a name="select"></a>

#### <code><span>Pane.select&#32;<span>cancellationToken&#32;pane</span></span></code>

Makes the pane its window&#39;s active pane, and returns a handle carrying the state afterwards.

The core&#39;s <code>Pane.SelectAsync</code> without a request; pass one to it to move by direction or keep the window&#39;s last pane.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Pane&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L312)

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

`TmuxPaneException` The pane&#39;s program had already exited, or the pane changed during every read until the timeout, so nothing was sent.

`TmuxObjectNotFoundException`tmux no longer has the pane.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L268)

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

`TmuxPaneException` The pane&#39;s program had already exited, or the pane changed during every read until the timeout, so nothing was sent.

`TmuxObjectNotFoundException`tmux no longer has the pane.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L277)

<a name="sendKeys"></a>

#### <code><span>Pane.sendKeys&#32;<span>cancellationToken&#32;request&#32;pane</span></span></code>

Sends text or key names according to the request&#39;s literal and Enter settings.

Cancellation can occur after dispatch; it does not undo sent keys.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>SendKeysRequest</code>

**pane**: <code>Pane</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L306)

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L296)

<a name="sendText"></a>

#### <code><span>Pane.sendText&#32;<span>cancellationToken&#32;text&#32;pane</span></span></code>

Types text into the pane literally, without pressing Enter.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**text**: <code>string</code>

**pane**: <code>Pane</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The text contains NUL.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L299)

<a name="setTitle"></a>

#### <code><span>Pane.setTitle&#32;<span>cancellationToken&#32;title&#32;pane</span></span></code>

Sets the pane's title and returns a handle carrying it.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**title**: <code>string</code>

**pane**: <code>Pane</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;Pane&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L318)

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L309)

<a name="swap"></a>

#### <code><span>Pane.swap&#32;<span>cancellationToken&#32;request&#32;pane</span></span></code>

Swaps the pane with another, as the request names it.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**request**: <code>SwapPaneRequest</code>

**pane**: <code>Pane</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L324)

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

`TmuxObjectNotFoundException`tmux no longer has the pane.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L265)

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

`TmuxObjectNotFoundException`tmux no longer has the pane.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L262)

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

`TmuxObjectNotFoundException`tmux no longer has the pane.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L285)
