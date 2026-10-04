## Options module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Reads and writes options through keys that know their value&#39;s type.

Pass the options of the scope the option belongs to, such as
 <code>session.Options</code> for <code>TmuxOptionKey.HistoryLimit</code>. Declare other keys
 with <code>TmuxOptionKey.Text</code>, <code>Number</code> or <code>Flag</code>.

### Functions and values

<a name="get"></a>

#### <code><span>Options.get&#32;<span>cancellationToken&#32;key&#32;options</span></span></code>

Reads the value an option has in a scope, set there or inherited, as its key&#39;s type.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**key**: <code><span>TmuxOptionKey&lt;'T&gt;</span></code>

**options**: <code>TmuxOptions</code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'T&gt;</span></code>

Type parameters: 'T

`TmuxOptionException`tmux rejected the name, reported no value, or reported one the key cannot read.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L260)

<a name="set"></a>

#### <code><span>Options.set&#32;<span>cancellationToken&#32;key&#32;value&#32;options</span></span></code>

Sets an option in a scope from a value of its key&#39;s type.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**key**: <code><span>TmuxOptionKey&lt;'T&gt;</span></code>

**value**: <code>'T</code>

**options**: <code>TmuxOptions</code>

Returns: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task">Task</a></code>

Type parameters: 'T

`TmuxOptionException`tmux rejected the name or the value.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L263)
