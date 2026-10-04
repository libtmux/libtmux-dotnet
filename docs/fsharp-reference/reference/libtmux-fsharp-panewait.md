## PaneWait module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Recognises how a wait on a pane's output ended.

### Active patterns

<a name="(%7cFound%7cPrinted%7cStopped%7cTimedOut%7cEnded%7c)"></a>

#### <code><span>PaneWait.(|Found|Printed|Stopped|TimedOut|Ended|)&#32;<span>result</span></span></code>

Tells how a wait ended, one case per kind of ending, so a match that leaves one out draws a warning.

<code>Found</code>: the text or a pattern appeared, before or during the wait.
 <code>Printed</code>: a wait with no pattern saw the pane print something.
 <code>Stopped</code>: a stop pattern matched, carried as its text.
 <code>TimedOut</code>: the time allowed ran out.
 <code>Ended</code>: the pane&#39;s program exited, or a full-screen program took over.

**Parameters:**

**result**: <code>PaneWaitResult</code>

Returns: <code><span><a href="https://fsharp.github.io/fsharp-core-docs/reference/fsharp-core-fsharpchoice-5">Choice</a>&lt;<span>unit,&#32;unit,&#32;string,&#32;unit,&#32;unit</span>&gt;</span></code>

[ArgumentOutOfRangeException](https://learn.microsoft.com/dotnet/api/system.argumentoutofrangeexception) The outcome is not one this facade knows.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L101)
