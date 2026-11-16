# release-checks

The checks `release.yml` runs around the packages it publishes. Each is a
command, so a failed step names the check:

- `tag` checks that the run is at a `v*` tag and that the tag names the version
  MSBuild reports for the library.
- `unpack` unpacks each package into a directory of its own, for the SBOM
  generator to read.
- `sbom` checks that the generated SBOM lists every package as a library.
- `fetch-published` fetches each package from nuget.org as it is served, and
  checks it carries the repository signature. nuget.org can take a while to serve
  what was just pushed, so it retries for thirty minutes.

```console
$ uv run scripts/release-checks/release_checks.py tag --ref-type tag --tag v0.1.2
```

```console
$ uv run scripts/release-checks/release_checks.py sbom --packages artifacts/packages --sbom artifacts/sbom-cyclonedx.json
```

A failed check prints a `::error::` annotation and exits 1. The tests build
small packages and serve them from a directory, so they need no registry.

Run the tests with the [script runner](../README.md#running-the-checks).
