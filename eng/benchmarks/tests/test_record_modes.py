"""A benchmark record must state the method the run actually used."""
import json
import pathlib
import runpy
import subprocess

import pytest

RECORDER = pathlib.Path(__file__).parents[1] / "record_modes.py"


def test_git_failure_cannot_supply_record_provenance(monkeypatch):
    recorder = runpy.run_path(str(RECORDER))

    def fail(*_args, **_kwargs):
        raise subprocess.CalledProcessError(128, ["git", "rev-parse", "HEAD"])

    with monkeypatch.context() as patch:
        patch.setattr(subprocess, "run", fail)
        with pytest.raises(SystemExit, match="git"):
            recorder["git"]("rev-parse", "HEAD")


def report(path, warmups):
    def benchmark(method, warmup):
        measurements = [{"IterationMode": "Workload", "IterationStage": "Warmup"}] * warmup
        measurements += [{"IterationMode": "Workload", "IterationStage": "Actual"}] * 3
        return {
            "Method": method,
            "Parameters": "Commands=1",
            "Statistics": {"OriginalValues": [1_000_000.0, 2_000_000.0, 3_000_000.0]},
            "Measurements": measurements,
        }

    path.write_text(json.dumps({"Benchmarks": [benchmark(f"Mode{index}", warmup) for index, warmup in enumerate(warmups)]}))
    return path


def test_warmup_count_comes_from_the_report(tmp_path):
    # Not forty, the count the benchmark configures, so a recorder that
    # restated the configuration instead of reading the report would fail.
    recorder = runpy.run_path(str(RECORDER))
    record = recorder["collect"](report(tmp_path / "full.json", [7, 7]), "3.7b", "2026-09-26")
    assert record["method"]["warmupSamples"] == 7


def test_cases_that_discarded_different_counts_are_refused(tmp_path):
    recorder = runpy.run_path(str(RECORDER))
    with pytest.raises(SystemExit, match="different warmup counts"):
        recorder["collect"](report(tmp_path / "full.json", [7, 40]), "3.7b", "2026-09-26")


def test_a_report_without_measurements_is_refused(tmp_path):
    recorder = runpy.run_path(str(RECORDER))
    path = report(tmp_path / "full.json", [40])
    document = json.loads(path.read_text())
    del document["Benchmarks"][0]["Measurements"]
    path.write_text(json.dumps(document))
    with pytest.raises(SystemExit, match="Measurements"):
        recorder["collect"](path, "3.7b", "2026-09-26")


def test_five_mode_record_requires_complete_samples_and_preflight(tmp_path, monkeypatch):
    recorder = runpy.run_path(str(RECORDER))
    commit = recorder["git"]("rev-parse", "HEAD")
    assert len(commit) == 40
    modes = {
        "serial-process": (24, 0),
        "concurrent-process": (24, 0),
        "process-chain": (1, 0),
        "serial-control": (0, 1),
        "concurrent-control": (0, 1),
    }
    cases = []
    log = []
    for mode, (processes, clients) in modes.items():
        cases.append({
            "Method": "Workload",
            "Parameters": f"Mode={mode}",
            "Statistics": {"OriginalValues": [1_000_000.0, 2_000_000.0, 3_000_000.0]},
            "Measurements": [
                {"IterationMode": "Workload", "IterationStage": "Warmup"},
                {"IterationMode": "Workload", "IterationStage": "Actual"},
            ],
            "Memory": {"BytesAllocatedPerOperation": 128},
        })
        log.append(f"// Benchmark: ModeWorkloadBenchmarks.Workload: Job-X [Mode={mode}]")
        log.append(
            f"Mode '{mode}' preflight: {processes} tmux processes, {clients} control clients, "
            "24 logical commands, concurrency cap 4."
        )
        log.append(f"Mode '{mode}' source: commit {commit}; clean true.")
    log.append("Run time: 00:00:02, executed benchmarks: 5")
    report_path = tmp_path / "full.json"
    log_path = tmp_path / "run.log"
    report_path.write_text(json.dumps({"Benchmarks": cases}))
    log_path.write_text("\n".join(log))

    record = recorder["collect"](report_path, "3.7d", "2026-09-27", log_path)
    assert record["schema"] == "libtmux-mode-workload-record-v1"
    assert len(record["cases"]) == 5
    assert {case["processes"] for case in record["cases"]} == {0, 1, 24}
    assert all(case["samples"] == 3 for case in record["cases"])
    assert record["method"]["modeOrder"] == list(modes)
    assert "Concurrent control" in recorder["render"](record)

    missing_source = [line for line in log if "'serial-process' source:" not in line]
    log_path.write_text("\n".join(missing_source))
    with pytest.raises(SystemExit, match="source"):
        recorder["collect"](report_path, "3.7d", "2026-09-27", log_path)

    wrong_source = [
        line.replace(commit, "0" * 40) if "'serial-process' source:" in line else line
        for line in log
    ]
    log_path.write_text("\n".join(wrong_source))
    with pytest.raises(SystemExit, match="source"):
        recorder["collect"](report_path, "3.7d", "2026-09-27", log_path)

    dirty_source = [line.replace("clean true", "clean false") for line in log]
    log_path.write_text("\n".join(dirty_source))
    with pytest.raises(SystemExit, match="source"):
        recorder["collect"](report_path, "3.7d", "2026-09-27", log_path)

    log_path.write_text("\n".join(log))

    with monkeypatch.context() as patch:
        patch.setitem(recorder["collect"].__globals__, "git", lambda *arguments: "")
        with pytest.raises(SystemExit, match="source"):
            recorder["collect"](report_path, "3.7d", "2026-09-27", log_path)

    cases.pop()
    report_path.write_text(json.dumps({"Benchmarks": cases}))
    with pytest.raises(SystemExit, match="five modes"):
        recorder["collect"](report_path, "3.7d", "2026-09-27", log_path)


RUNS = pathlib.Path(__file__).parents[3] / "docs" / "benchmarks" / "runs"


@pytest.mark.parametrize("record", sorted(RUNS.glob("*.json")), ids=lambda path: path.stem)
def test_a_published_table_is_its_record_rendered(record):
    # The table is what gets read and quoted; it must say what the record
    # says, field for field, however either was last edited.
    recorder = runpy.run_path(str(RECORDER))
    assert record.with_suffix(".md").read_text(encoding="utf-8") == recorder["render"](
        json.loads(record.read_text(encoding="utf-8"))
    )
