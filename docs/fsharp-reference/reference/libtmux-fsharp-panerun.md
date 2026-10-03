## PaneRun module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Recognises how a command run with <code>Pane.run</code> ended.

### Active patterns

<a name="(%7cExited%7c_%7c)"></a>

#### <code><span>PaneRun.(|Exited|_|)&#32;<span>result</span></span></code>

Matches a command that exited, with its exit status.

**Parameters:**

**result**: <code>PaneRunResult</code>

Returns: <code><span>int&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L77)

<a name="(%7cNotStarted%7c_%7c)"></a>

#### <code><span>PaneRun.(|NotStarted|_|)&#32;<span>result</span></span></code>

Matches a command the pane's shell never ran.

**Parameters:**

**result**: <code>PaneRunResult</code>

Returns: <code><span>unit&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L82)

<a name="(%7cTimedOut%7c_%7c)"></a>

#### <code><span>PaneRun.(|TimedOut|_|)&#32;<span>result</span></span></code>

Matches a command still running when the time allowed ran out.

**Parameters:**

**result**: <code>PaneRunResult</code>

Returns: <code><span>unit&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L79)
