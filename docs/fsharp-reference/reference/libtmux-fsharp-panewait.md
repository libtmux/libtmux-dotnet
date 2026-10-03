## PaneWait module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Recognises how a wait on a pane's output ended.

### Active patterns

<a name="(%7cEnded%7c_%7c)"></a>

#### <code><span>PaneWait.(|Ended|_|)&#32;<span>result</span></span></code>

Matches a wait that ended because the pane's program exited or a full-screen program took over.

**Parameters:**

**result**: <code>PaneWaitResult</code>

Returns: <code><span>unit&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L106)

<a name="(%7cFound%7c_%7c)"></a>

#### <code><span>PaneWait.(|Found|_|)&#32;<span>result</span></span></code>

Matches a wait whose text or pattern appeared, before or during it.

**Parameters:**

**result**: <code>PaneWaitResult</code>

Returns: <code><span>unit&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L86)

<a name="(%7cPrinted%7c_%7c)"></a>

#### <code><span>PaneWait.(|Printed|_|)&#32;<span>result</span></span></code>

Matches a wait with no pattern that ended because the pane printed something.

**Parameters:**

**result**: <code>PaneWaitResult</code>

Returns: <code><span>unit&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L88)

<a name="(%7cStopped%7c_%7c)"></a>

#### <code><span>PaneWait.(|Stopped|_|)&#32;<span>result</span></span></code>

Matches a wait a stop pattern ended, with the pattern that matched.

**Parameters:**

**result**: <code>PaneWaitResult</code>

Returns: <code><span>string&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L94)

<a name="(%7cTimedOut%7c_%7c)"></a>

#### <code><span>PaneWait.(|TimedOut|_|)&#32;<span>result</span></span></code>

Matches a wait whose time ran out.

**Parameters:**

**result**: <code>PaneWaitResult</code>

Returns: <code><span>unit&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L100)
