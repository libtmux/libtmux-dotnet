## Owned module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Runs tasks with an explicitly owned tmux resource.

### Functions and values

<a name="withResource"></a>

#### <code><span>Owned.withResource&#32;<span>cancellationToken&#32;work&#32;owner</span></span></code>

Runs work and awaits destruction of the owned resource after success, failure or cancellation.

The token is checked before work starts; pass it to commands inside work to cancel them.
 Cleanup has its own five-second deadline and retains the captured endpoint, daemon generation and object ID.
 If work and cleanup both fail, the original exception and cancellation token propagate;
 <code>Control.cleanupFailure</code> returns the cleanup exception. A failed cleanup can be retried on the owner.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**work**: <code><span>'Resource&#32;->&#32;<span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'State&gt;</span></span></code>

**owner**: <code><span>IOwnedTmuxResource&lt;'Resource&gt;</span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'State&gt;</span></code>

Type parameters: 'Resource, 'State

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L13)
