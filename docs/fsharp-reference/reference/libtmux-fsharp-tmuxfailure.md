## TmuxFailure module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Recognises tmux failures by whether running the operation again could repeat what it did.

Every <code>LibTmuxException</code> says whether its command reached tmux, so match
 on that rather than on the exception type. <code>NotSent</code> is the only failure
 after which running the same operation again is always safe.

### Active patterns

<a name="(%7cMayHaveRun%7c_%7c)"></a>

#### <code><span>TmuxFailure.(|MayHaveRun|_|)&#32;<span>error</span></span></code>

Matches a failure, or a cancellation, after which tmux may already have acted.

**Parameters:**

**error**: <code>exn</code>

Returns: <code><span>exn&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L69)

<a name="(%7cNotSent%7c_%7c)"></a>

#### <code><span>TmuxFailure.(|NotSent|_|)&#32;<span>error</span></span></code>

Matches a failure whose command never reached tmux; running it again repeats nothing.

**Parameters:**

**error**: <code>exn</code>

Returns: <code><span>LibTmuxException&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L59)

<a name="(%7cRefused%7c_%7c)"></a>

#### <code><span>TmuxFailure.(|Refused|_|)&#32;<span>error</span></span></code>

Matches a failure tmux answered: it ran the command, which refused or reported an error.

**Parameters:**

**error**: <code>exn</code>

Returns: <code><span>LibTmuxException&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L64)
