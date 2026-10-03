## SessionSpec type

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Base Type: <code>obj</code>

All Interfaces: <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralequatable">IStructuralEquatable</a></code>, <code><span><a href="https://learn.microsoft.com/dotnet/api/system.icomparable-1">IComparable</a>&lt;<a href="../reference/libtmux-fsharp-sessionspec.md">SessionSpec</a>&gt;</span></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.icomparable">IComparable</a></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralcomparable">IStructuralComparable</a></code>

Describes a session and its windows, for <code>Server.newSession</code>.

tmux gives a new session one window, which the first <code>WindowSpec</code>
 becomes; each later one is a window of its own. A session with no windows
 listed gets tmux&#39;s single default window.

### Record fields

<a name="Directory"></a>

#### <code>Directory</code>

The working directory of the session and its first window.

Field type: <code><span>string&#32;option</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L54)

<a name="Environment"></a>

#### <code>Environment</code>

Variables added to the session's environment.

Field type: <code><span><a href="https://fsharp.github.io/fsharp-core-docs/reference/fsharp-collections-fsharpmap-2">Map</a>&lt;<span>string,&#32;string</span>&gt;</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L56)

<a name="Name"></a>

#### <code>Name</code>

The session's name.

Field type: <code>string</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L52)

<a name="Windows"></a>

#### <code>Windows</code>

The windows, in order; the first is the one tmux creates with the session.

Field type: <code><span><a href="../reference/libtmux-fsharp-windowspec.md">WindowSpec</a>&#32;list</span></code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Spec.fsi#L58)
