## WindowFields module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Provides supported window fields and relations for portable filters.

### Functions and values

<a name="height"></a>

#### <code><span>WindowFields.height&#32;<span></span></span></code>

Identifies the window's height in cells.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L270)

<a name="id"></a>

#### <code><span>WindowFields.id&#32;<span></span></span></code>

Identifies the typed physical window ID.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;WindowId</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L267)

<a name="index"></a>

#### <code><span>WindowFields.index&#32;<span></span></span></code>

Identifies where the window sits in its session.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L268)

<a name="name"></a>

#### <code><span>WindowFields.name&#32;<span></span></span></code>

Identifies the window name.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L266)

<a name="paneCount"></a>

#### <code><span>WindowFields.paneCount&#32;<span></span></span></code>

Identifies the number of panes in the window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L271)

<a name="panes"></a>

#### <code><span>WindowFields.panes&#32;<span></span></span></code>

Identifies panes captured through this window placement.

Returns: <code><span><a href="../reference/libtmux-fsharp-relation-2.md">Relation</a>&lt;<span>Window,&#32;Pane</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L272)

<a name="width"></a>

#### <code><span>WindowFields.width&#32;<span></span></span></code>

Identifies the window's width in cells.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L269)
