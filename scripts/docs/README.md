# docs

Generators and checks for documents that quote code. A generated document is
downstream of code that compiles, so `--check` fails when it has drifted.

| Script | Job |
| --- | --- |
| `render_api_reference.py` | Render the C# and F# API reference from `artifacts/api-inventory.json` |
| `sync_snippets.py` | Copy `#region` blocks from `examples/LibTmux.Examples/Snippets/` into the documents that publish them |
| `sync_fsharp_snippets.py` | The same for compiled F# guide blocks; `--expect-output NAME` compares a program's real output with its guide |
| `sync_fsdocs_reference.py` | Generate the F# member pages from `fsdocs` and the Release XML documentation |
| `verify_api_examples.py` | Validate the whole-file API examples in `examples/api/` and run their package consumers |

`dotnet.yml` runs each with `--check`. Run one without `--check` to rewrite the
documents it owns:

```console
$ uv run python scripts/docs/sync_snippets.py
```

[examples/README.md](../../examples/README.md) describes the snippet markers.
Run the tests with the [script runner](../README.md#running-the-checks).
