## PaneWatch module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Recognises what a pane watch yields.

### Active patterns

<a name="(%7cOutput%7cPaused%7cContinued%7cDropped%7cGone%7cExited%7c)"></a>

#### <code><span>PaneWatch.(|Output|Paused|Continued|Dropped|Gone|Exited|)&#32;<span>event</span></span></code>

Tells what <code>Control.watchPane</code> or <code>Control.watchPanes</code> yielded, one case per kind of event, so a match that leaves one out draws a warning.

<code>Output</code>: text a watched pane printed, carried as its event.
 <code>Paused</code>: tmux stopped sending that pane&#39;s output, so what it prints until <code>Continued</code> never arrives; capture the pane to read its screen.
 <code>Continued</code>: tmux resumed sending that pane&#39;s output.
 <code>Dropped</code>: a full buffer discarded events, carried as the loss report.
 <code>Gone</code>: a watched pane is gone; the watch ends once every pane is.
 <code>Exited</code>: the control client ended, with tmux&#39;s reason when it gave one; the watch ends.

**Parameters:**

**event**: <code>TmuxEvent</code>

Returns: <code><span><a href="https://fsharp.github.io/fsharp-core-docs/reference/fsharp-core-fsharpchoice-6">Choice</a>&lt;<span>TmuxOutputEvent,&#32;PaneId,&#32;PaneId,&#32;TmuxEventsDroppedEvent,&#32;PaneId,&#32;<span>string&#32;option</span></span>&gt;</span></code>

[ArgumentOutOfRangeException](https://learn.microsoft.com/dotnet/api/system.argumentoutofrangeexception) The event is not one a pane watch yields.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L109)
