# matrix-evidence

Records the tmux version matrix at a release boundary. A capability row in
`docs/parity/version-deltas.json` is `pending` until a matrix run records
evidence for it and `verified` after, for one exact source tree.
[CONTRIBUTING.md](../../.github/CONTRIBUTING.md#recorded-evidence-is-a-release-artifact)
says why recording belongs at a release rather than in the gate, and gives the
commands.

| Script | Job |
| --- | --- |
| `run_matrix.py` | Build the solution, run one test project against every supported tmux version and framework, and publish the lanes as an evidence bundle |
| `reconcile_versions.py` | Validate a recorded matrix and write the verified rows into `version-deltas.json` |
| `assemble_bundle.py` | Create, publish or discard a candidate evidence bundle as one transaction, and fingerprint the source tree |
| `validate.py` | Validate a bundle: sanitized, complete, bound to one commit |

`run_matrix.py` reads the versions from `eng/tmux/versions.json` and the .NET SDK
from `global.json`. It needs a tmux build per version at
`<root>/<version>/bin/tmux`, which `--tmux-root` names, and reads each release
tag's commit from the tmux repository; `LIBTMUX_TMUX_REPOSITORY` points it at a
mirror. `LIBTMUX_MATRIX_MAX_THREADS` sets how many test collections run at once.
`LIBTMUX_MATRIX_LANE_TIMEOUT` sets a lane's ceiling in seconds, 1800 by default;
a lane that passes it fails, and its processes stop, as they do on an
interrupt. The tag and version lookups give up after a minute.

Run the tests with the [script runner](../README.md#running-the-checks).
