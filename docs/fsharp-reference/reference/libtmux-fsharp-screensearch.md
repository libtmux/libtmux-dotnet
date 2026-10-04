## ScreenSearch type

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Base Type: <code>obj</code>

All Interfaces: <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralequatable">IStructuralEquatable</a></code>, <code><span><a href="https://learn.microsoft.com/dotnet/api/system.icomparable-1">IComparable</a>&lt;<a href="../reference/libtmux-fsharp-screensearch.md">ScreenSearch</a>&gt;</span></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.icomparable">IComparable</a></code>, <code><a href="https://learn.microsoft.com/dotnet/api/system.collections.istructuralcomparable">IStructuralComparable</a></code>

Describes text tmux searches for on a pane&#39;s visible rows.

tmux evaluates the search itself, as <code>find-window -C</code> does: it reads
 only the rows on screen, with trailing spaces removed. To search history,
 capture the pane and filter its lines.

### Union cases

<a name="PosixRegex"></a>

#### <code><span>PosixRegex&#32;pattern</span></code>

Matches a POSIX extended regular expression, which tmux evaluates.

**Parameters:**

**pattern**: <code>string</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fsi#L126)

<a name="PosixRegexIgnoringCase"></a>

#### <code><span>PosixRegexIgnoringCase&#32;pattern</span></code>

Matches a POSIX extended regular expression ignoring case.

**Parameters:**

**pattern**: <code>string</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fsi#L128)

<a name="Text"></a>

#### <code><span>Text&#32;text</span></code>

Matches literal text.

**Parameters:**

**text**: <code>string</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fsi#L122)

<a name="TextIgnoringCase"></a>

#### <code><span>TextIgnoringCase&#32;text</span></code>

Matches literal text ignoring case.

**Parameters:**

**text**: <code>string</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fsi#L124)

### Instance members

<a name="IsPosixRegex"></a>

#### <code><span>this.IsPosixRegex</span></code>

Returns: <code>bool</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L164)

<a name="IsPosixRegexIgnoringCase"></a>

#### <code><span>this.IsPosixRegexIgnoringCase</span></code>

Returns: <code>bool</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L165)

<a name="IsText"></a>

#### <code><span>this.IsText</span></code>

Returns: <code>bool</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L162)

<a name="IsTextIgnoringCase"></a>

#### <code><span>this.IsTextIgnoringCase</span></code>

Returns: <code>bool</code>

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L163)
