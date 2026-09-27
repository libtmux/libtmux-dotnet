## Window module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Identifies window placements without refreshing their captured state.

### Functions and values

<a name="placementKey"></a>

#### <code><span>Window.placementKey&#32;<span>window</span></span></code>

Returns a comparable key including the captured session and window index.

**Parameters:**

**window**: <code>Window</code>

Returns: <code><a href="../reference/libtmux-fsharp-windowplacementkey.md">WindowPlacementKey</a></code>

`IncompleteSnapshotException` The placement was not captured.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Library.fs#L57)
