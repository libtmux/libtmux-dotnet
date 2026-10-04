## SplitSize type

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Base Type: <code>obj</code>

All Interfaces: <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralequatable">IStructuralEquatable</a></code>, <code><span><a href="https://learn.microsoft.com/dotnet/api/system.icomparable-1">IComparable</a>&lt;<a href="../reference/libtmux-fsharp-splitsize.md">SplitSize</a>&gt;</span></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.icomparable">IComparable</a></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralcomparable">IStructuralComparable</a></code>

How much of the space split a new pane takes.

### Union cases

<a name="Cells"></a>

#### <code><span>Cells&#32;cells</span></code>

A number of cells: columns for a split beside, rows for one above or below; at least 1.

**Parameters:**

**cells**: <code>int</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L9)

<a name="Percent"></a>

#### <code><span>Percent&#32;percent</span></code>

A share of the space split, from 1 to 100.

**Parameters:**

**percent**: <code>int</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L11)

### Instance members

<a name="IsCells"></a>

#### <code><span>this.IsCells</span></code>

Returns: <code>bool</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fs#L11)

<a name="IsPercent"></a>

#### <code><span>this.IsPercent</span></code>

Returns: <code>bool</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fs#L12)
