## Filter module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Constructs portable predicates through the core query translator.

### Functions and values

<a name="all"></a>

#### <code><span>Filter.all&#32;<span>relation&#32;predicate</span></span></code>

Requires every child to match, returning true for an empty captured relation.

**Parameters:**

**relation**: <code><span><a href="../reference/libtmux-fsharp-relation-2.md">Relation</a>&lt;<span>'Parent,&#32;'Child</span>&gt;</span></code>

**predicate**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'Child&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'Parent&gt;</span></code>

Type parameters: 'Parent, 'Child

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L127)

<a name="allOf"></a>

#### <code><span>Filter.allOf&#32;<span>filters</span></span></code>

Requires every predicate in a nonempty list, in input order.

**Parameters:**

**filters**: <code><span><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span>&#32;list</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The filters list is empty.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L109)

<a name="any"></a>

#### <code><span>Filter.any&#32;<span>relation&#32;predicate</span></span></code>

Requires a matching child, returning false for an empty captured relation.

**Parameters:**

**relation**: <code><span><a href="../reference/libtmux-fsharp-relation-2.md">Relation</a>&lt;<span>'Parent,&#32;'Child</span>&gt;</span></code>

**predicate**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'Child&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'Parent&gt;</span></code>

Type parameters: 'Parent, 'Child

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L124)

<a name="anyOf"></a>

#### <code><span>Filter.anyOf&#32;<span>filters</span></span></code>

Requires at least one predicate in a nonempty list, in input order.

**Parameters:**

**filters**: <code><span><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span>&#32;list</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The filters list is empty.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L112)

<a name="eq"></a>

#### <code><span>Filter.eq&#32;<span>value&#32;field</span></span></code>

Matches a field against a constant using the core's equality semantics.

**Parameters:**

**value**: <code>'Value</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;'Value</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'Value, 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L76)

<a name="isNull"></a>

#### <code><span>Filter.isNull&#32;<span>field</span></span></code>

Matches a captured null string without treating an uncaptured field as absent.

**Parameters:**

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;string</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L84)

<a name="negate"></a>

#### <code><span>Filter.negate&#32;<span>filter</span></span></code>

Negates a portable predicate.

**Parameters:**

**filter**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L121)

<a name="none"></a>

#### <code><span>Filter.none&#32;<span>relation&#32;predicate</span></span></code>

Requires no matching child, returning true for an empty captured relation.

**Parameters:**

**relation**: <code><span><a href="../reference/libtmux-fsharp-relation-2.md">Relation</a>&lt;<span>'Parent,&#32;'Child</span>&gt;</span></code>

**predicate**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'Child&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'Parent&gt;</span></code>

Type parameters: 'Parent, 'Child

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L130)

<a name="oneOf"></a>

#### <code><span>Filter.oneOf&#32;<span>values&#32;field</span></span></code>

Matches any constant in a nonempty list, preserving operand order.

**Parameters:**

**values**: <code><span>'Value&#32;list</span></code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;'Value</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'Value, 'T

[ArgumentException](https://learn.microsoft.com/dotnet/api/system.argumentexception) The values list is empty.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L115)

<a name="startsWith"></a>

#### <code><span>Filter.startsWith&#32;<span>prefix&#32;field</span></span></code>

Matches a string prefix using ordinal comparison.

**Parameters:**

**prefix**: <code>string</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;string</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L92)

<a name="toDocument"></a>

#### <code><span>Filter.toDocument&#32;<span>filter</span></span></code>

Returns the core document validated when the filter was constructed.

**Parameters:**

**filter**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Returns: <code>QueryDocument</code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L131)

<a name="toPredicate"></a>

#### <code><span>Filter.toPredicate&#32;<span>filter</span></span></code>

Compiles once and returns a predicate for native lazy filtering.

The predicate has no node-level cancellation token.

**Parameters:**

**filter**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Returns: <code><span>'T&#32;->&#32;bool</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L133)
