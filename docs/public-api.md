# API policy

The Roslyn analyzer baselines in each library's `PublicAPI.Shipped.txt` and
`PublicAPI.Unshipped.txt` define the approved declarations. The compiler
inventory under `artifacts/` supplies exact XML IDs and visibility to the
[API reference](api/README.md) and parity checks.

[`public-api.json`](public-api.json) records source attribution, package
responsibilities, ownership, and I/O classifications that the compiler cannot
infer. Its member IDs are compiler XML documentation IDs. Add policy metadata
only when an API introduces a policy classification; ordinary declarations
need only their analyzer baseline update.

The [parity ledger](parity/parity-ledger.json) maps Python symbols to those
compiler IDs or records an explicit exclusion. Public and internal
classifications must agree with compiler visibility. Borrowed handles cannot
implement disposal; public owned scopes implement `IAsyncDisposable`. I/O
methods return tasks and take a final optional cancellation token. Public
process-backed APIs carry their Windows platform restriction on the member
or containing type.

Ownership and immutable replacement examples are in the
[one-shot guide](modes/one-shot.md). The [JSON package README](../src/LibTmux.Query.Json/README.md)
shows query round trips. Those examples compile through the documentation
suite; runtime equality, cancellation, generation, failure and platform tests
provide the behavioral evidence.

Generate the inventory after building the engineering tool:

```console
$ mise exec -- dotnet run \
    --project eng/LibTmux.Engineering \
    --configuration Release \
    --no-build \
    -- api-inventory . artifacts/api-inventory.json
```

Check policy and regenerate the reference:

```console
$ uv run python eng/parity/verify_public_api.py
```

```console
$ uv run python eng/docs/render_api_reference.py
```
