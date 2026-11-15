# package-review

`package_review.py` packs and inspects one immutable prerelease from a clean
committed checkout, for a downstream review. It runs the restore, build,
compiler inventory, pack and package inspection, records timings and archive
hashes, and writes `provenance.json` only after inspection and a final
integrity check.

Run each phase separately with the same SDK, environment, version and output
directory. [CONTRIBUTING.md](../../.github/CONTRIBUTING.md#packages-and-standalone-consumers)
has the commands.

`tests/test_packages.py` mutates real Release archives and runs the native
package inspector. It is tagged `packaging` and needs `LIBTMUX_PACKAGE_ARTIFACTS`
to name a pack, so the ordinary run skips it; `dotnet.yml` runs it after the
pack step.

Run the tests with the [script runner](../README.md#running-the-checks).
