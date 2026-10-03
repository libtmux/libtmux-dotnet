## WindowFields module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Provides supported window fields and relations for portable filters.

### Functions and values

<a name="active"></a>

#### <code><span>WindowFields.active&#32;<span></span></span></code>

Identifies whether the window is the current window of the session it was read through.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L289)

<a name="activityAlert"></a>

#### <code><span>WindowFields.activityAlert&#32;<span></span></span></code>

Identifies whether the window printed since it was last the current window, while monitor-activity is on.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L292)

<a name="bellAlert"></a>

#### <code><span>WindowFields.bellAlert&#32;<span></span></span></code>

Identifies whether a bell rang in the window since it was last the current window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L291)

<a name="height"></a>

#### <code><span>WindowFields.height&#32;<span></span></span></code>

Identifies the window's height in cells.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L288)

<a name="id"></a>

#### <code><span>WindowFields.id&#32;<span></span></span></code>

Identifies the typed physical window ID.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;WindowId</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L285)

<a name="index"></a>

#### <code><span>WindowFields.index&#32;<span></span></span></code>

Identifies where the window sits in its session.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L286)

<a name="name"></a>

#### <code><span>WindowFields.name&#32;<span></span></span></code>

Identifies the window name.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L284)

<a name="paneCount"></a>

#### <code><span>WindowFields.paneCount&#32;<span></span></span></code>

Identifies the number of panes in the window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L294)

<a name="panes"></a>

#### <code><span>WindowFields.panes&#32;<span></span></span></code>

Identifies panes captured through this window placement.

Returns: <code><span><a href="../reference/libtmux-fsharp-relation-2.md">Relation</a>&lt;<span>Window,&#32;Pane</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L295)

<a name="silenceAlert"></a>

#### <code><span>WindowFields.silenceAlert&#32;<span></span></span></code>

Identifies whether the window has been silent for monitor-silence seconds.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L293)

<a name="width"></a>

#### <code><span>WindowFields.width&#32;<span></span></span></code>

Identifies the window's width in cells.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L287)

<a name="zoomed"></a>

#### <code><span>WindowFields.zoomed&#32;<span></span></span></code>

Identifies whether one of the window's panes is zoomed to fill it.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L290)
