## TmuxAsync module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Awaits tasks in an <code>async</code> workflow without losing whether tmux may have acted.

<code>Async.AwaitTask</code> turns a <code>TmuxOperationCanceledException</code> into a bare
 <code>TaskCanceledException</code>, losing <code>CommandMayHaveExecuted</code>, and wraps a failure in an
 <code>AggregateException</code>. These raise a tmux client cancelled after it may have acted as itself,
 so <code>TmuxFailure.MayHaveRun</code> matches it in <code>try ... with</code>; any other cancellation cancels
 the workflow, and a failure is raised as the task raised it.

### Functions and values

<a name="awaitTask"></a>

#### <code><span>TmuxAsync.awaitTask&#32;<span>task</span></span></code>

Awaits a task that returns a value.

**Parameters:**

**task**: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'T&gt;</span></code>

Returns: <code><span><a href="https://fsharp.github.io/fsharp-core-docs/reference/fsharp-control-fsharpasync-1">Async</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L147)

<a name="awaitUnitTask"></a>

#### <code><span>TmuxAsync.awaitUnitTask&#32;<span>task</span></span></code>

Awaits a task that returns nothing.

**Parameters:**

**task**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

Returns: <code><span><a href="https://fsharp.github.io/fsharp-core-docs/reference/fsharp-control-fsharpasync-1">Async</a>&lt;unit&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L158)
