## StreamStep<'State> type

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Base Type: <code>obj</code>

All Interfaces: <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralequatable">IStructuralEquatable</a></code>, <code><span><a href="https://learn.microsoft.com/dotnet/api/system.icomparable-1">IComparable</a>&lt;<span><a href="../reference/libtmux-fsharp-streamstep-1.md">StreamStep</a>&lt;'State&gt;</span>&gt;</span></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.icomparable">IComparable</a></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralcomparable">IStructuralComparable</a></code>

Represents a decision to continue or stop an event fold.

### Union cases

<a name="Continue"></a>

#### <code><span>Continue&#32;state</span></code>

Retains state and reads the next event.

**Parameters:**

**state**: <code>'State</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fsi#L12)

<a name="Stop"></a>

#### <code><span>Stop&#32;state</span></code>

Retains state and stops before reading another event.

**Parameters:**

**state**: <code>'State</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fsi#L14)

### Instance members

<a name="IsContinue"></a>

#### <code><span>this.IsContinue</span></code>

Returns: <code>bool</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L12)

<a name="IsStop"></a>

#### <code><span>this.IsStop</span></code>

Returns: <code>bool</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Control.fs#L13)
