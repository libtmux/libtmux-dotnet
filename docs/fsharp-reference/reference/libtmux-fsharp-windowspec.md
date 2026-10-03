## WindowSpec type

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Base Type: <code>obj</code>

All Interfaces: <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralequatable">IStructuralEquatable</a></code>, <code><span><a href="https://learn.microsoft.com/dotnet/api/system.icomparable-1">IComparable</a>&lt;<a href="../reference/libtmux-fsharp-windowspec.md">WindowSpec</a>&gt;</span></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.icomparable">IComparable</a></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralcomparable">IStructuralComparable</a></code>

Describes a window: its first pane, then each pane split off the one before.

Build one from <code>WindowSpec.named</code> or <code>WindowSpec.empty</code> with a copy-and-update expression.

### Record fields

<a name="Command"></a>

#### <code>Command</code>

The command the first pane runs instead of the default shell.

Field type: <code><span>string&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L31)

<a name="Directory"></a>

#### <code>Directory</code>

The first pane's working directory.

Field type: <code><span>string&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L33)

<a name="Environment"></a>

#### <code>Environment</code>

Variables added to the first pane's environment; a session's first window takes them from the session instead.

Field type: <code><span><a href="https://fsharp.github.io/fsharp-core-docs/reference/fsharp-collections-fsharpmap-2">Map</a>&lt;<span>string,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L35)

<a name="Name"></a>

#### <code>Name</code>

The window's name; tmux names it after its command when None.

Field type: <code><span>string&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L29)

<a name="Splits"></a>

#### <code>Splits</code>

The panes split off in order, each beside the pane before it.

Field type: <code><span><a href="../reference/libtmux-fsharp-splitspec.md">SplitSpec</a>&#32;list</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L37)
