# actionlint

`run_actionlint.py` runs [actionlint](https://github.com/rhysd/actionlint) over
the workflows at the version `.tool-versions` pins. It downloads the Linux
x86-64 release archive and checks it against a SHA-256 digest before it
unpacks anything, so a replaced release asset fails the run. The "Lint
workflows" step of the `build and prove` job in `dotnet.yml` runs it with the
digest in `ACTIONLINT_SHA256`.

Bump actionlint by changing the version in `.tool-versions` and the digest in
`dotnet.yml` together. Run it from a checkout on Linux x86-64:

```console
$ uv run scripts/actionlint/run_actionlint.py --sha256 DIGEST
```

Elsewhere, run the `actionlint` that `mise` installs from `.tool-versions`.
The exit status is actionlint's, or 1 when the download or the digest check
fails.

Run the tests with the [script runner](../README.md#running-the-checks).
