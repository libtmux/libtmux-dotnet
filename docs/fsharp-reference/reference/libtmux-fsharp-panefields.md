## PaneFields module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Provides supported pane fields for portable filters.

### Functions and values

<a name="atBottom"></a>

#### <code><span>PaneFields.atBottom&#32;<span></span></span></code>

Identifies whether the pane touches the bottom of its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L283)

<a name="atLeft"></a>

#### <code><span>PaneFields.atLeft&#32;<span></span></span></code>

Identifies whether the pane touches the left of its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L284)

<a name="atRight"></a>

#### <code><span>PaneFields.atRight&#32;<span></span></span></code>

Identifies whether the pane touches the right of its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L285)

<a name="atTop"></a>

#### <code><span>PaneFields.atTop&#32;<span></span></span></code>

Identifies whether the pane touches the top of its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L282)

<a name="currentCommand"></a>

#### <code><span>PaneFields.currentCommand&#32;<span></span></span></code>

Identifies the captured command, including a captured unavailable value.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L273)

<a name="currentPath"></a>

#### <code><span>PaneFields.currentPath&#32;<span></span></span></code>

Identifies the pane's working directory, as the text tmux reported.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L277)

<a name="height"></a>

#### <code><span>PaneFields.height&#32;<span></span></span></code>

Identifies the pane's height in cells.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L279)

<a name="id"></a>

#### <code><span>PaneFields.id&#32;<span></span></span></code>

Identifies the typed pane ID.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;PaneId</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L274)

<a name="index"></a>

#### <code><span>PaneFields.index&#32;<span></span></span></code>

Identifies the pane's position in its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L275)

<a name="left"></a>

#### <code><span>PaneFields.left&#32;<span></span></span></code>

Identifies the column of the pane's left edge in its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L280)

<a name="title"></a>

#### <code><span>PaneFields.title&#32;<span></span></span></code>

Identifies the pane's title, which a program running in it can set.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L276)

<a name="top"></a>

#### <code><span>PaneFields.top&#32;<span></span></span></code>

Identifies the row of the pane's top edge in its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L281)

<a name="width"></a>

#### <code><span>PaneFields.width&#32;<span></span></span></code>

Identifies the pane's width in cells.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L278)
