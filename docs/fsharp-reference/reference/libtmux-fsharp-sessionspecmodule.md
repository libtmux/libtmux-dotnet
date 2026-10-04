## SessionSpec module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Starts session descriptions.

### Functions and values

<a name="named"></a>

#### <code><span>SessionSpec.named&#32;<span>name</span></span></code>

A named session with tmux's single default window.

**Parameters:**

**name**: <code>string</code>

Returns: <code><a href="../reference/libtmux-fsharp-sessionspec.md">SessionSpec</a></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fs#L79)

<a name="running"></a>

#### <code><span>SessionSpec.running&#32;<span>name&#32;command</span></span></code>

A named session whose one window runs a command instead of the default shell.

Such as <code>SessionSpec.running &quot;build&quot; &quot;/bin/sh&quot;</code>, for a shell that <code>Pane.run</code> accepts whatever the user&#39;s login shell is.

**Parameters:**

**name**: <code>string</code>

**command**: <code>string</code>

Returns: <code><a href="../reference/libtmux-fsharp-sessionspec.md">SessionSpec</a></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fs#L87)
