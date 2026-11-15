# psmux-smoke

`psmux_smoke.py` checks the [psmux preview](../../docs/psmux.md) against one
audited `psmux.exe`. A maintainer runs it by hand on a Windows x64 machine with
WSL; CI does not run psmux, and `release.yml` does not wait for it.

It verifies the binary's SHA-256 and embedded build markers before the first
launch, starts one session in a fresh namespace, prints a UTF-8 fixture into it,
and runs the unit-test smoke, the example and the packed consumer against that
server, natively and, with `--run-wsl-smoke`, through WSL. It removes only the
exact session it created and the data directory it owns, and a failing cleanup
is a failing run. [docs/psmux.md](../../docs/psmux.md#native-windows-and-wsl-smoke)
has the build steps and the full command.

Run it through `uv`, which installs the Windows-only `psutil` dependency:

```console
$ uv run scripts/psmux-smoke/psmux_smoke.py --help
```

The tests run on any platform. They stand in for `psmux.exe`, `wsl.exe`, the
drive table and the process table, so they prove the verification order, the
exact-identity cleanup and the WSL arguments, but not psmux itself.

Run the tests with the [script runner](../README.md#running-the-checks).
