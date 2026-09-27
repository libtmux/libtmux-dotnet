## Control module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Provides scoped access to core control-mode event streams.

### Functions and values

<a name="enter"></a>

#### <code><span>Control.enter&#32;<span>cancellationToken&#32;server</span></span></code>

Opens a core control client with the caller&#39;s cancellation token.

The caller owns and asynchronously disposes the returned client.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**server**: <code>Server</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;IControlModeSession&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L61)

<a name="foldEventsWhile"></a>

#### <code><span>Control.foldEventsWhile&#32;<span>cancellationToken&#32;folder&#32;initial&#32;session</span></span></code>

Folds events until the source ends or the folder returns Stop.

The helper disposes its enumerator but leaves the control client open.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**folder**: <code><span>'State&#32;->&#32;TmuxEvent&#32;->&#32;<span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;<span><a href="../reference/libtmux-fsharp-streamstep-1.md">StreamStep</a>&lt;'State&gt;</span>&gt;</span></span></code>

**initial**: <code>'State</code>

**session**: <code>IControlModeSession</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'State&gt;</span></code>

Type parameters: 'State

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L92)

<a name="iterEvents"></a>

#### <code><span>Control.iterEvents&#32;<span>cancellationToken&#32;handler&#32;session</span></span></code>

Awaits one handler at a time for each event from a borrowed control client.

The helper disposes its enumerator but leaves the control client open.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**handler**: <code><span>TmuxEvent&#32;->&#32;<a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></span></code>

**session**: <code>IControlModeSession</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;unit&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L78)

<a name="useSession"></a>

#### <code><span>Control.useSession&#32;<span>work&#32;session</span></span></code>

Runs work with an owned control client and disposes it after the returned task completes.

The work function receives the client and must forward its own cancellation token.

**Parameters:**

**work**: <code><span>IControlModeSession&#32;->&#32;<span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'State&gt;</span></span></code>

**session**: <code>IControlModeSession</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'State&gt;</span></code>

Type parameters: 'State

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L64)

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

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L68)
