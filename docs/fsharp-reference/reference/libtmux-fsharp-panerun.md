## PaneRun module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Recognises how a command run with <code>Pane.run</code> ended.

### Active patterns

<a name="(%7cExited%7cEnded%7cNotStarted%7cTimedOut%7c)"></a>

#### <code><span>PaneRun.(|Exited|Ended|NotStarted|TimedOut|)&#32;<span>result</span></span></code>

Tells how a run ended, one case per kind of ending, so a match that leaves one out draws a warning.

<code>Exited</code>: the command exited, carried as its exit status.
 <code>Ended</code>: the pane&#39;s program exited before the command reported its status, and the run ended then.
 <code>NotStarted</code>: the pane&#39;s shell never ran it, such as when the pane was not at a prompt.
 <code>TimedOut</code>: the time allowed ran out first; the command may still be running.

**Parameters:**

**result**: <code>PaneRunResult</code>

Returns: <code><span><a href="https://fsharp.github.io/fsharp-core-docs/reference/fsharp-core-fsharpchoice-4">Choice</a>&lt;<span>int,&#32;unit,&#32;unit,&#32;unit</span>&gt;</span></code>

[ArgumentOutOfRangeException](https://learn.microsoft.com/dotnet/api/system.argumentoutofrangeexception) The result has none of these endings.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L84)
