## Filter module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Constructs portable predicates without reflection.

<p class='fsdocs-para'>
 String operations compare ordinally; the <code>IgnoreCase</code> forms use ordinal
 case-insensitive comparison. tmux evaluates the case-sensitive string,
 equality, flag, identifier and count operations itself when a listing
 pushes the filter down; every result is rechecked with these semantics.
 </p>

### Functions and values

<a name="all"></a>

#### <code><span>Filter.all&#32;<span>relation&#32;predicate</span></span></code>

Requires every child to match, returning true for an empty captured relation.

**Parameters:**

**relation**: <code><span><a href="../reference/libtmux-fsharp-relation-2.md">Relation</a>&lt;<span>'Parent,&#32;'Child</span>&gt;</span></code>

**predicate**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'Child&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'Parent&gt;</span></code>

Type parameters: 'Parent, 'Child

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L148)

<a name="allOf"></a>

#### <code><span>Filter.allOf&#32;<span>filters</span></span></code>

Requires every predicate in order; an empty list matches everything.

**Parameters:**

**filters**: <code><span><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span>&#32;list</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L128)

<a name="any"></a>

#### <code><span>Filter.any&#32;<span>relation&#32;predicate</span></span></code>

Requires a matching child, returning false for an empty captured relation.

**Parameters:**

**relation**: <code><span><a href="../reference/libtmux-fsharp-relation-2.md">Relation</a>&lt;<span>'Parent,&#32;'Child</span>&gt;</span></code>

**predicate**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'Child&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'Parent&gt;</span></code>

Type parameters: 'Parent, 'Child

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L145)

<a name="anyOf"></a>

#### <code><span>Filter.anyOf&#32;<span>filters</span></span></code>

Requires at least one predicate in order; an empty list matches nothing.

**Parameters:**

**filters**: <code><span><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span>&#32;list</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L134)

<a name="contains"></a>

#### <code><span>Filter.contains&#32;<span>text&#32;field</span></span></code>

Matches a string containing a substring.

**Parameters:**

**text**: <code>string</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;string</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L104)

<a name="containsIgnoreCase"></a>

#### <code><span>Filter.containsIgnoreCase&#32;<span>text&#32;field</span></span></code>

Matches a string containing a substring ignoring case.

**Parameters:**

**text**: <code>string</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;string</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L107)

<a name="endsWith"></a>

#### <code><span>Filter.endsWith&#32;<span>suffix&#32;field</span></span></code>

Matches a string suffix.

**Parameters:**

**suffix**: <code>string</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;string</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L98)

<a name="endsWithIgnoreCase"></a>

#### <code><span>Filter.endsWithIgnoreCase&#32;<span>suffix&#32;field</span></span></code>

Matches a string suffix ignoring case.

**Parameters:**

**suffix**: <code>string</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;string</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L101)

<a name="eq"></a>

#### <code><span>Filter.eq&#32;<span>value&#32;field</span></span></code>

Matches a field equal to a constant.

**Parameters:**

**value**: <code>'Value</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;'Value</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'Value, 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L77)

<a name="eqIgnoreCase"></a>

#### <code><span>Filter.eqIgnoreCase&#32;<span>value&#32;field</span></span></code>

Matches a string equal to a constant ignoring case.

**Parameters:**

**value**: <code>string</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;string</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L86)

<a name="ge"></a>

#### <code><span>Filter.ge&#32;<span>value&#32;field</span></span></code>

Matches a count at least a constant.

**Parameters:**

**value**: <code>int</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;int</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L125)

<a name="gt"></a>

#### <code><span>Filter.gt&#32;<span>value&#32;field</span></span></code>

Matches a count above a constant.

**Parameters:**

**value**: <code>int</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;int</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L122)

<a name="isNull"></a>

#### <code><span>Filter.isNull&#32;<span>field</span></span></code>

Matches a captured null string without treating an uncaptured field as absent.

**Parameters:**

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;string</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L89)

<a name="le"></a>

#### <code><span>Filter.le&#32;<span>value&#32;field</span></span></code>

Matches a count at most a constant.

**Parameters:**

**value**: <code>int</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;int</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L119)

<a name="lt"></a>

#### <code><span>Filter.lt&#32;<span>value&#32;field</span></span></code>

Matches a count below a constant.

**Parameters:**

**value**: <code>int</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;int</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L116)

<a name="matches"></a>

#### <code><span>Filter.matches&#32;<span>pattern&#32;field</span></span></code>

Matches a string against a culture-invariant .NET regular expression.

The pattern is unanchored, as <code>Regex.IsMatch</code> is; one match may run for one second.

**Parameters:**

**pattern**: <code>string</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;string</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

`UnsupportedQueryExpressionException` The pattern is invalid or longer than 1024 characters.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L110)

<a name="matchesIgnoreCase"></a>

#### <code><span>Filter.matchesIgnoreCase&#32;<span>pattern&#32;field</span></span></code>

Matches a string against a culture-invariant .NET regular expression ignoring case.

**Parameters:**

**pattern**: <code>string</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;string</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

`UnsupportedQueryExpressionException` The pattern is invalid or longer than 1024 characters.

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L113)

<a name="ne"></a>

#### <code><span>Filter.ne&#32;<span>value&#32;field</span></span></code>

Matches a field not equal to a constant.

**Parameters:**

**value**: <code>'Value</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;'Value</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'Value, 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L84)

<a name="negate"></a>

#### <code><span>Filter.negate&#32;<span>filter</span></span></code>

Negates a portable predicate.

**Parameters:**

**filter**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L83)

<a name="none"></a>

#### <code><span>Filter.none&#32;<span>relation&#32;predicate</span></span></code>

Requires no matching child, returning true for an empty captured relation.

**Parameters:**

**relation**: <code><span><a href="../reference/libtmux-fsharp-relation-2.md">Relation</a>&lt;<span>'Parent,&#32;'Child</span>&gt;</span></code>

**predicate**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'Child&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'Parent&gt;</span></code>

Type parameters: 'Parent, 'Child

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L151)

<a name="notOneOf"></a>

#### <code><span>Filter.notOneOf&#32;<span>values&#32;field</span></span></code>

Matches no constant in a list; an empty list matches everything.

**Parameters:**

**values**: <code><span>'Value&#32;list</span></code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;'Value</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'Value, 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L143)

<a name="oneOf"></a>

#### <code><span>Filter.oneOf&#32;<span>values&#32;field</span></span></code>

Matches any constant in a list; an empty list matches nothing.

**Parameters:**

**values**: <code><span>'Value&#32;list</span></code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;'Value</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'Value, 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L140)

<a name="startsWith"></a>

#### <code><span>Filter.startsWith&#32;<span>prefix&#32;field</span></span></code>

Matches a string prefix.

**Parameters:**

**prefix**: <code>string</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;string</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L92)

<a name="startsWithIgnoreCase"></a>

#### <code><span>Filter.startsWithIgnoreCase&#32;<span>prefix&#32;field</span></span></code>

Matches a string prefix ignoring case.

**Parameters:**

**prefix**: <code>string</code>

**field**: <code><span><a href="../reference/libtmux-fsharp-field-2.md">Field</a>&lt;<span>'T,&#32;string</span>&gt;</span></code>

Returns: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L95)

<a name="toDocument"></a>

#### <code><span>Filter.toDocument&#32;<span>filter</span></span></code>

Returns the core document validated when the filter was constructed.

**Parameters:**

**filter**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Returns: <code>QueryDocument</code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L152)

<a name="toPredicate"></a>

#### <code><span>Filter.toPredicate&#32;<span>filter</span></span></code>

Returns a predicate compiled once for native lazy filtering.

**Parameters:**

**filter**: <code><span><a href="../reference/libtmux-fsharp-filter-1.md">Filter</a>&lt;'T&gt;</span></code>

Returns: <code><span>'T&#32;->&#32;bool</span></code>

Type parameters: 'T

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Query.fs#L153)
