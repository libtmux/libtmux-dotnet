## Snapshot module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Reads captured values without contacting tmux.

### Functions and values

<a name="relation"></a>

#### <code><span>Snapshot.relation&#32;<span>relation</span></span></code>

Distinguishes captured children from an unread relation.

**Parameters:**

**relation**: <code><span>CapturedRelation&lt;'T&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-capturestate-1.md">CaptureState</a>&lt;<span><a href="https://learn.microsoft.com/dotnet/api/system.collections.generic.ireadonlylist-1">IReadOnlyList</a>&lt;'T&gt;</span>&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L28)

<a name="value"></a>

#### <code><span>Snapshot.value&#32;<span>value</span></span></code>

Distinguishes a captured child from an unread value.

**Parameters:**

**value**: <code><span>CapturedValue&lt;'T&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-capturestate-1.md">CaptureState</a>&lt;'T&gt;</span></code>

Type parameters: 'T (requires not struct)

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L34)
