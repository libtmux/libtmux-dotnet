## CardinalityError type

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Base Type: <code>obj</code>

All Interfaces: <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralequatable">IStructuralEquatable</a></code>, <code><span><a href="https://learn.microsoft.com/dotnet/api/system.icomparable-1">IComparable</a>&lt;<a href="../reference/libtmux-fsharp-cardinalityerror.md">CardinalityError</a>&gt;</span></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.icomparable">IComparable</a></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralcomparable">IStructuralComparable</a></code>

Describes a selection that does not contain exactly one match.

### Union cases

<a name="MultipleMatches"></a>

#### <code><span>MultipleMatches</span></code>

At least two elements matched.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fsi#L13)

<a name="NoMatches"></a>

#### <code><span>NoMatches</span></code>

No element matched.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fsi#L11)

### Instance members

<a name="IsMultipleMatches"></a>

#### <code><span>this.IsMultipleMatches</span></code>

Returns: <code>bool</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L11)

<a name="IsNoMatches"></a>

#### <code><span>this.IsNoMatches</span></code>

Returns: <code>bool</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L10)
