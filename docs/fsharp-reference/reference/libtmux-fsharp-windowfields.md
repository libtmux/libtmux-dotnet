## WindowFields module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Provides supported window fields and relations for portable filters.

### Functions and values

<a name="id"></a>

#### <code><span>WindowFields.id&#32;<span></span></span></code>

Identifies the typed physical window ID.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;WindowId</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L172)

<a name="name"></a>

#### <code><span>WindowFields.name&#32;<span></span></span></code>

Identifies the window name.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L171)

<a name="paneCount"></a>

#### <code><span>WindowFields.paneCount&#32;<span></span></span></code>

Identifies the number of panes in the window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Window,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L173)

<a name="panes"></a>

#### <code><span>WindowFields.panes&#32;<span></span></span></code>

Identifies panes captured through this window placement.

Returns: <code><span><a href="../reference/libtmux-fsharp-relation-2.md">Relation</a>&lt;<span>Window,&#32;Pane</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L174)
