## Retry module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Runs an operation again only when tmux never saw it.

### Functions and values

<a name="ifNotSent"></a>

#### <code><span>Retry.ifNotSent&#32;<span>cancellationToken&#32;retries&#32;operation</span></span></code>

Runs an operation, and again up to <code>retries</code> times while nothing it sent reached tmux.

An attempt is repeated only when it fails with <code>NotSent</code> and no
 command it sent before that failure reached tmux, counting every command
 the operation awaits, including through nested retries. An operation of
 several steps whose first step ran is therefore not repeated because a
 later step was refused before dispatch. Any other failure, and
 cancellation, propagates at once. A read that is safe to repeat whatever
 happened belongs in the caller&#39;s own retry policy.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**retries**: <code>int</code>

**operation**: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a>&#32;->&#32;<span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'T&gt;</span></span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[ArgumentOutOfRangeException](https://learn.microsoft.com/dotnet/api/system.argumentoutofrangeexception) The retry count is negative.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L122)

<a name="ifNotSentAfter"></a>

#### <code><span>Retry.ifNotSentAfter&#32;<span>cancellationToken&#32;delays&#32;operation</span></span></code>

Runs an operation, and after each delay in turn runs it again while nothing it sent reached tmux.

Retries as <code>ifNotSent</code> does, once per delay, waiting that long
 first, so a server still starting has time to answer:
 <code>Retry.ifNotSentAfter ct [ TimeSpan.FromMilliseconds 100.; TimeSpan.FromMilliseconds 400. ] operation</code>.
 Cancellation during a delay propagates.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**delays**: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.timespan">TimeSpan</a>&#32;list</span></code>

**operation**: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a>&#32;->&#32;<span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'T&gt;</span></span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[ArgumentOutOfRangeException](https://learn.microsoft.com/dotnet/api/system.argumentoutofrangeexception) A delay is negative.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L128)
