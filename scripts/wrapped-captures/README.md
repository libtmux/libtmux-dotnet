# wrapped-captures

`generate_wrapped_captures.py` regenerates
[`tests/Fixtures/wrapped-captures.json`](../../tests/Fixtures/wrapped-captures.json)
from real tmux. It prints each screen into a 20-column pane and captures it
twice, as rows and as joined lines. The unit tests pair the two, so they need no
tmux at test time. A maintainer runs it when the screens change, with the oldest
and newest supported tmux:

```console
$ uv run python scripts/wrapped-captures/generate_wrapped_captures.py \
    tests/Fixtures/wrapped-captures.json \
    /path/to/tmux-3.3a \
    /path/to/tmux-3.8
```

Run the tests with the [script runner](../README.md#running-the-checks).
