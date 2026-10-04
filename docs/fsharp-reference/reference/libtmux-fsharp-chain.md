## Chain module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Builds commands tmux runs together, each acting on what the one before made.

<p class='fsdocs-para'>
 tmux moves its current target as a chain runs: <code>newWindow</code> makes the
 new window current, a following split splits its pane, and a following
 <code>sendLine</code> types into the pane that split made. No step after the
 first names a target, so a chain needs no round trip to learn the id of
 what it just created.
 </p><p class='fsdocs-para'>
 A chain is a core <code>TmuxChain</code>: each step returns a new one, building
 reads nothing, and <code>run</code> sends every command in one tmux invocation.
 <code>add</code> appends any command, such as a typed request&#39;s <code>ToCommand</code>.
 </p>

### Functions and values

<a name="add"></a>

#### <code><span>Chain.add&#32;<span>command&#32;chain</span></span></code>

Appends any command, such as a typed request&#39;s <code>ToCommand</code>.

**Parameters:**

**command**: <code>TmuxCommand</code>

**chain**: <code>TmuxChain</code>

Returns: <code>TmuxChain</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L290)

<a name="arrange"></a>

#### <code><span>Chain.arrange&#32;<span>layout&#32;chain</span></span></code>

Arranges the current window with a tmux layout; the chain checks the name before tmux sees it.

**Parameters:**

**layout**: <code>string</code>

**chain**: <code>TmuxChain</code>

Returns: <code>TmuxChain</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L287)

<a name="newWindow"></a>

#### <code><span>Chain.newWindow&#32;<span>session&#32;name&#32;chain</span></span></code>

Adds a window to a session and makes it the one following steps act on.

It fails rather than reach another session when tmux restarted after the session was read.

**Parameters:**

**session**: <code>Session</code>

**name**: <code>string</code>

**chain**: <code>TmuxChain</code>

Returns: <code>TmuxChain</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L270)

<a name="run"></a>

#### <code><span>Chain.run&#32;<span>cancellationToken&#32;chain</span></span></code>

Runs every command in one tmux invocation and returns tmux&#39;s combined answer.

Cancellation after dispatch does not undo commands tmux already ran.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**chain**: <code>TmuxChain</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;TmuxCommandResult&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L292)

<a name="sendLine"></a>

#### <code><span>Chain.sendLine&#32;<span>line&#32;chain</span></span></code>

Types a line into the current pane and presses Enter.

**Parameters:**

**line**: <code>string</code>

**chain**: <code>TmuxChain</code>

Returns: <code>TmuxChain</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L284)

<a name="splitLeftRight"></a>

#### <code><span>Chain.splitLeftRight&#32;<span>chain</span></span></code>

Splits the current pane into a left and a right one; the right becomes current.

**Parameters:**

**chain**: <code>TmuxChain</code>

Returns: <code>TmuxChain</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L280)

<a name="splitTopBottom"></a>

#### <code><span>Chain.splitTopBottom&#32;<span>chain</span></span></code>

Splits the current pane into a top and a bottom one; the bottom becomes current.

**Parameters:**

**chain**: <code>TmuxChain</code>

Returns: <code>TmuxChain</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L282)

<a name="start"></a>

#### <code><span>Chain.start&#32;<span>server</span></span></code>

Starts an empty chain against a server.

**Parameters:**

**server**: <code>Server</code>

Returns: <code>TmuxChain</code>

[InvalidOperationException](https://learn.microsoft.com/dotnet/api/system.invalidoperationexception) The server handle has no connection.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L268)
