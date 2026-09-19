# Examples

Every example here runs against a real tmux server of its own, as a test, on
every build. Nothing in this directory can quietly stop compiling or stop being
true — and because the READMEs quote from it, neither can they.

Two mechanisms sit on top of that, and they catch different failures:

| Check | Where | Fails when |
| --- | --- | --- |
| `ReadmeExampleTests` | `tests/LibTmux.IntegrationTests/Documentation/` | A C# block in a shipped document does not compile, or a `csharp run` block does not run |
| `sync_snippets.py` | `eng/docs/` | A published block has drifted from the region it was quoted from |
| `SnippetContractTests` | `tests/LibTmux.ExampleTests/` | A published region is not an example that runs, or an example is somewhere the snippet reader cannot see |

## Writing an example

An example is a method carrying `[Example]`, taking a `Server` and a
`CancellationToken`, and returning `Task`. It lives in a class under
[`LibTmux.Examples/Snippets/`](LibTmux.Examples/Snippets/) — one class per
topic, and the class name *is* the topic, so `Chaining.cs` holds the chaining
examples.

```csharp
/// <summary>Runs three commands through a single tmux invocation.</summary>
[Example("Three commands, one process")]
public static async Task ManyCommandsOneProcess(Server server, CancellationToken ct)
{
    #region ManyCommandsOneProcess
    await server.Chain()
        .Then("new-window", "-d", "-n", "build")
        .Then("new-window", "-d", "-n", "test")
        .ExecuteAsync(ct);
    #endregion
}
```

Run them all:

```console
$ mise exec -- dotnet run \
    --project examples/LibTmux.Examples/LibTmux.Examples.csproj \
    --configuration Release
```

## Quoting an example in a document

The `#region` name is what a document publishes, and it matches the method
name. Put an anchor pair where the block belongs and the region is copied
between them:

```markdown
<!-- snippet: ManyCommandsOneProcess -->
<!-- endsnippet -->
```

Then materialize it:

```console
$ uv run python eng/docs/sync_snippets.py
```

The region is dedented before it is written, so code nested inside a method
lands at column zero in the document. A document that needs a `using` the
snippet file already has at file scope can ask for it, and the directive is
written above the block:

```markdown
<!-- snippet: ConnectAndBuild usings: LibTmux -->
```

Nine documents publish snippets: the root README, each of the four package
READMEs, and the four files under `docs/modes/`. Decision records are
deliberately excluded — they quote what was run at the time, and an example
edited later to keep compiling records nothing.

### The loop runs one way

Edit the example, run it, then bring the copy across. **Never edit the block in
the document** — `--check` compares the two and fails the build on any
difference, so a hand-edited block is reported as drift rather than kept:

```console
$ uv run python eng/docs/sync_snippets.py --check
```

That is what CI runs.

### Both failure modes

`SnippetContractTests` fails a region that no `[Example]` method runs, because
a published block nobody executes is exactly the sample that rots. It also
fails an example that lives outside
[`LibTmux.Examples/Snippets/`](LibTmux.Examples/Snippets/), since that is the
one directory `sync_snippets.py` globs — an example anywhere else is invisible
to it and would never be published at all.

## Blocks that are not snippets

A C# block does not have to be a snippet. `ReadmeExampleTests` compiles every
`csharp` block in those same nine documents against the real assemblies, and
executes the ones tagged `csharp run` against a tmux server of their own:

````markdown
```csharp run
Window built = await session.CreateWindowAsync(new NewWindowRequest { Name = "build" }, ct);
```
````

Tag a block `csharp run` when it is meant to execute, and leave it plain
`csharp` when it only illustrates. A plain block still has to compile. The
harness supplies a preamble and hoists type declarations, so an example does
not repeat the setup — but everything else has to be real.
