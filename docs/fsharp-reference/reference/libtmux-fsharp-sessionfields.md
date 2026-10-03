## SessionFields module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Provides supported session fields and relations for portable filters.

### Functions and values

<a name="attached"></a>

#### <code><span>SessionFields.attached&#32;<span></span></span></code>

Identifies whether the captured session has attached clients.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Session,&#32;bool</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L260)

<a name="id"></a>

#### <code><span>SessionFields.id&#32;<span></span></span></code>

Identifies the typed session ID.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Session,&#32;SessionId</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L259)

<a name="name"></a>

#### <code><span>SessionFields.name&#32;<span></span></span></code>

Identifies the session name.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Session,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L258)

<a name="windowCount"></a>

#### <code><span>SessionFields.windowCount&#32;<span></span></span></code>

Identifies the number of windows linked into the session.

Returns: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>Session,&#32;int</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L261)

<a name="windows"></a>

#### <code><span>SessionFields.windows&#32;<span></span></span></code>

Identifies captured window placements within the session.

Returns: <code><span><a href="../reference/libtmux-fsharp-relation-2.md">Relation</a>&lt;<span>Session,&#32;Window</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L262)
