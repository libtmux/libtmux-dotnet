## Selection module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Selects values from ordinary F# sequences.

### Functions and values

<a name="exactlyOne"></a>

#### <code><span>Selection.exactlyOne&#32;<span>source</span></span></code>

Returns the sole match, examining at most two elements.

Disposes the enumerator on success, multiple matches or failure.

**Parameters:**

**source**: <code><span>'T&#32;seq</span></code>

Returns: <code><span><a href="https://fsharp.github.io/fsharp-core-docs/reference/fsharp-core-fsharpresult-2">Result</a>&lt;<span>'T,&#32;<a href="../reference/libtmux-fsharp-cardinalityerror.md">CardinalityError</a></span>&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L44)
