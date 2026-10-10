## FindOrCreate module

Namespace: [LibTmux.FSharp](../reference/libtmux-fsharp.md)

Assembly: LibTmux.FSharp.dll

Distinguishes borrowed matches from resources created and owned by find-or-create.

### Functions and values

<a name="withResource"></a>

#### <code><span>FindOrCreate.withResource&#32;<span>cancellationToken&#32;work&#32;result</span></span></code>

Runs work and destroys only a resource created by the find-or-create call.

An existing resource remains borrowed after success, failure or cancellation.
 Created resources use <code>Owned.withResource</code>, including its cancellation and paired-failure behavior.
 The token is checked before work starts; work passes it to its own cancellable commands.

**Parameters:**

**cancellationToken**: <code><a href="https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken">CancellationToken</a></code>

**work**: <code><span>'Resource&#32;->&#32;<span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'State&gt;</span></span></code>

**result**: <code><span>FoundOrCreated&lt;'Resource&gt;</span></code>

Returns: <code><span><a href="https://learn.microsoft.com/dotnet/api/system.threading.tasks.task-1">Task</a>&lt;'State&gt;</span></code>

Type parameters: 'Resource, 'State

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L31)

### Active patterns

<a name="(%7cExisting%7cCreated%7c)"></a>

#### <code><span>FindOrCreate.(|Existing|Created|)&#32;<span>result</span></span></code>

Matches an existing borrowed handle or the owner of a newly created resource.

Matching does not dispose the result. Dispose a created owner or pass the result to <code>withResource</code>.

**Parameters:**

**result**: <code><span>FoundOrCreated&lt;'Resource&gt;</span></code>

Returns: <code><span><a href="https://fsharp.github.io/fsharp-core-docs/reference/fsharp-core-fsharpchoice-2">Choice</a>&lt;<span>'Resource,&#32;<span>IOwnedTmuxResource&lt;'Resource&gt;</span></span>&gt;</span></code>

Type parameters: 'Resource

[Source](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/Hierarchy.fs#L26)
