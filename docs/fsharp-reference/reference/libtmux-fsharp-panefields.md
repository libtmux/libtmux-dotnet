## PaneFields module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Provides supported pane fields for portable filters.

### Functions and values

<a name="active"></a>

#### <code><span>PaneFields.active&#32;<span></span></span></code>

Identifies whether the pane is its window's active pane.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L309)

<a name="atBottom"></a>

#### <code><span>PaneFields.atBottom&#32;<span></span></span></code>

Identifies whether the pane touches the bottom of its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L306)

<a name="atLeft"></a>

#### <code><span>PaneFields.atLeft&#32;<span></span></span></code>

Identifies whether the pane touches the left of its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L307)

<a name="atRight"></a>

#### <code><span>PaneFields.atRight&#32;<span></span></span></code>

Identifies whether the pane touches the right of its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L308)

<a name="atTop"></a>

#### <code><span>PaneFields.atTop&#32;<span></span></span></code>

Identifies whether the pane touches the top of its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L305)

<a name="currentCommand"></a>

#### <code><span>PaneFields.currentCommand&#32;<span></span></span></code>

Identifies the captured command, including a captured unavailable value.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L296)

<a name="currentPath"></a>

#### <code><span>PaneFields.currentPath&#32;<span></span></span></code>

Identifies the pane's working directory, as the text tmux reported.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L300)

<a name="dead"></a>

#### <code><span>PaneFields.dead&#32;<span></span></span></code>

Identifies whether the pane's program has exited while the pane remains.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L310)

<a name="height"></a>

#### <code><span>PaneFields.height&#32;<span></span></span></code>

Identifies the pane's height in cells.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L302)

<a name="id"></a>

#### <code><span>PaneFields.id&#32;<span></span></span></code>

Identifies the typed pane ID.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;PaneId</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L297)

<a name="inMode"></a>

#### <code><span>PaneFields.inMode&#32;<span></span></span></code>

Identifies whether the pane is in a mode, such as copy mode.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L311)

<a name="index"></a>

#### <code><span>PaneFields.index&#32;<span></span></span></code>

Identifies the pane's position in its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L298)

<a name="left"></a>

#### <code><span>PaneFields.left&#32;<span></span></span></code>

Identifies the column of the pane's left edge in its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L303)

<a name="processId"></a>

#### <code><span>PaneFields.processId&#32;<span></span></span></code>

Identifies the process ID of the program the pane started.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L312)

<a name="synchronized"></a>

#### <code><span>PaneFields.synchronized&#32;<span></span></span></code>

Identifies whether keys typed into the pane go to every synchronized pane in its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L313)

<a name="title"></a>

#### <code><span>PaneFields.title&#32;<span></span></span></code>

Identifies the pane's title, which a program running in it can set.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L299)

<a name="top"></a>

#### <code><span>PaneFields.top&#32;<span></span></span></code>

Identifies the row of the pane's top edge in its window.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L304)

<a name="width"></a>

#### <code><span>PaneFields.width&#32;<span></span></span></code>

Identifies the pane's width in cells.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Pane,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L301)
