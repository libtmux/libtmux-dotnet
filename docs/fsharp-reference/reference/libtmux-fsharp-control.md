## Control module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Opens control clients and reads their event streams.

Streams are cold: nothing is read until a consumer enumerates one, and the
 consumer supplies the cancellation token. Any <code>IAsyncEnumerable</code>
 library composes them, including FSharp.Control.TaskSeq.

### Functions and values

<a name="cleanupFailure"></a>

#### <code><span>Control.cleanupFailure&#32;<span>error</span></span></code>

Returns the cleanup failure attached to the exception a helper rethrew.

When work and cleanup both fail, the helpers rethrow the work&#39;s exception
 unchanged and attach the cleanup&#39;s; this reads it back.

**Parameters:**

**error**: <code>exn</code>

Returns: <code><span>exn&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L131)

<a name="enter"></a>

#### <code><span>Control.enter&#32;<span>cancellationToken&#32;server</span></span></code>

Opens a control client attached to the most recently used session.

The caller owns and asynchronously disposes the returned client.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;IControlModeSession&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L53)

<a name="enterSession"></a>

#### <code><span>Control.enterSession&#32;<span>cancellationToken&#32;session</span></span></code>

Opens a control client attached to a session.

The caller owns and asynchronously disposes the returned client.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**session**: <code>Session</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;IControlModeSession&gt;</span></code>

`IncompleteSnapshotException` The session was not read through a server.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L56)

<a name="events"></a>

#### <code><span>Control.events&#32;<span>session</span></span></code>

Streams every event a control client reports.

A client has one event stream; two consumers each see only part of it.

**Parameters:**

**session**: <code>IControlModeSession</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.iasyncenumerable-1">IAsyncEnumerable</a>&lt;TmuxEvent&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L72)

<a name="foldWhile"></a>

#### <code><span>Control.foldWhile&#32;<span>cancellationToken&#32;folder&#32;initial&#32;source</span></span></code>

Folds items until the stream ends or the folder returns Stop.

The helper disposes its enumerator but leaves the control client open.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**folder**: <code><span>'State&#32;->&#32;'T&#32;->&#32;<span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span><a href="../reference/libtmux-fsharp-streamstep-1.md">StreamStep</a>&lt;'State&gt;</span>&gt;</span></span></code>

**initial**: <code>'State</code>

**source**: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.iasyncenumerable-1">IAsyncEnumerable</a>&lt;'T&gt;</span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'State&gt;</span></code>

Type parameters: 'State, 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L103)

<a name="iter"></a>

#### <code><span>Control.iter&#32;<span>cancellationToken&#32;handler&#32;source</span></span></code>

Awaits one handler at a time for each item until the stream ends.

The helper disposes its enumerator but leaves the control client open.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**handler**: <code><span>'T&#32;->&#32;<a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></span></code>

**source**: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.iasyncenumerable-1">IAsyncEnumerable</a>&lt;'T&gt;</span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;unit&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L89)

<a name="useSession"></a>

#### <code><span>Control.useSession&#32;<span>work&#32;session</span></span></code>

Runs work with an owned control client and disposes it after the returned task completes.

The work function receives the client and must forward its own cancellation token.

**Parameters:**

**work**: <code><span>IControlModeSession&#32;->&#32;<span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'State&gt;</span></span></code>

**session**: <code>IControlModeSession</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'State&gt;</span></code>

Type parameters: 'State

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L59)

<a name="watchPane"></a>

#### <code><span>Control.watchPane&#32;<span>pane&#32;session</span></span></code>

Streams one pane&#39;s output from a borrowed control client.

<p class='fsdocs-para'>
 The stream ends with <code>TmuxPaneGoneEvent</code> once the pane is confirmed
 gone, or with <code>TmuxExitEvent</code> when the client ends.
 <code>TmuxPanePausedEvent</code> and <code>TmuxPaneContinuedEvent</code> bracket output
 a slow reader missed. It reads the client&#39;s single event stream, so other
 events are consumed and dropped.
 </p><p class='fsdocs-para'>
 tmux discards output it has not yet sent once a pane&#39;s program exits,
 so the last lines of a program that exits at once may never arrive.
 Read final output with <code>Pane.run</code>, or capture a pane kept with
 <code>remain-on-exit</code>.
 </p>

**Parameters:**

**pane**: <code>Pane</code>

**session**: <code>IControlModeSession</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.iasyncenumerable-1">IAsyncEnumerable</a>&lt;TmuxEvent&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L74)

<a name="withSession"></a>

#### <code><span>Control.withSession&#32;<span>cancellationToken&#32;work&#32;server</span></span></code>

Opens a control client, runs work, and disposes the client after the returned task completes.

The cancellation token starts the client; the work function forwards its own token.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**work**: <code><span>IControlModeSession&#32;->&#32;<span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'State&gt;</span></span></code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'State&gt;</span></code>

Type parameters: 'State

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L62)
