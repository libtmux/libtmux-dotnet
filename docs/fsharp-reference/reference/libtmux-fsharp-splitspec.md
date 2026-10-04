## SplitSpec type

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Base Type: <code>obj</code>

All Interfaces: <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralequatable">IStructuralEquatable</a></code>, <code><span><a href="https://learn.microsoft.com/dotnet/api/system.icomparable-1">IComparable</a>&lt;<a href="../reference/libtmux-fsharp-splitspec.md">SplitSpec</a>&gt;</span></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.icomparable">IComparable</a></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralcomparable">IStructuralComparable</a></code>

Describes a pane split off the pane created before it.

Build one from <code>SplitSpec.empty</code> with a copy-and-update expression.

### Record fields

<a name="Command"></a>

#### <code>Command</code>

The command the pane runs instead of the default shell.

Field type: <code><span>string&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L23)

<a name="Direction"></a>

#### <code>Direction</code>

Where the new pane goes, beside the pane before it; tmux puts it below when None.

Field type: <code><span>PaneDirection&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L21)

<a name="Directory"></a>

#### <code>Directory</code>

The pane's working directory.

Field type: <code><span>string&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L25)

<a name="Environment"></a>

#### <code>Environment</code>

Variables added to the pane's environment.

Field type: <code><span><a href="https://fsharp.github.io/fsharp-core-docs/reference/fsharp-collections-fsharpmap-2">Map</a>&lt;<span>string,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L29)

<a name="Size"></a>

#### <code>Size</code>

The pane&#39;s size, such as <code>SplitSize.Cells 20</code> or <code>SplitSize.Percent 30</code>; tmux splits in half when None.

Field type: <code><span><a href="../reference/libtmux-fsharp-splitsize.md">SplitSize</a>&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L27)
