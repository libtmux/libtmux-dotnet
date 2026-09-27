## CaptureState<'T> type

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Base Type: <code>obj</code>

All Interfaces: <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralequatable">IStructuralEquatable</a></code>, <code><span><a href="https://learn.microsoft.com/dotnet/api/system.icomparable-1">IComparable</a>&lt;<span><a href="../reference/libtmux-fsharp-capturestate-1.md">CaptureState</a>&lt;'T&gt;</span>&gt;</span></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.icomparable">IComparable</a></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralcomparable">IStructuralComparable</a></code>

Distinguishes captured state from a relation the snapshot did not read.

### Union cases

<a name="Captured"></a>

#### <code><span>Captured&#32;value</span></code>

Contains the captured value, including an observed empty collection.

**Parameters:**

**value**: <code>'T</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fsi#L18)

<a name="Uncaptured"></a>

#### <code><span>Uncaptured(<span>relation,&#32;depth</span>)</span></code>

Names the unread relation and the depth the snapshot reached.

**Parameters:**

**relation**: <code>string</code>

**depth**: <code>SnapshotDepth</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fsi#L20)

### Instance members

<a name="IsCaptured"></a>

#### <code><span>this.IsCaptured</span></code>

Returns: <code>bool</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L12)

<a name="IsUncaptured"></a>

#### <code><span>this.IsUncaptured</span></code>

Returns: <code>bool</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L13)
