## Retry module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Runs an operation again only when tmux never saw it.

### Functions and values

<a name="ifNotSent"></a>

#### <code><span>Retry.ifNotSent&#32;<span>cancellationToken&#32;retries&#32;operation</span></span></code>

Runs an operation, and again up to <code>retries</code> times while it fails before reaching tmux.

Any other failure, and cancellation, propagates at once. A read that is
 safe to repeat whatever happened belongs in the caller&#39;s own retry policy.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**retries**: <code>int</code>

**operation**: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a>&#32;->&#32;<span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'T&gt;</span></span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[ArgumentOutOfRangeException](https://learn.microsoft.com/dotnet/api/system.argumentoutofrangeexception) The retry count is negative.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L77)
