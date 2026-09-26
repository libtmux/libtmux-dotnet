"""A benchmark record must state the method the run actually used."""
import json
import pathlib
import runpy

import pytest

RECORDER = pathlib.Path(__file__).parents[1] / "record_modes.py"


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


RUNS = pathlib.Path(__file__).parents[3] / "docs" / "benchmarks" / "runs"


@pytest.mark.parametrize("record", sorted(RUNS.glob("*.json")), ids=lambda path: path.stem)
def test_a_published_table_is_its_record_rendered(record):
    # The table is what gets read and quoted; it must say what the record
    # says, field for field, however either was last edited.
    recorder = runpy.run_path(str(RECORDER))
    assert record.with_suffix(".md").read_text(encoding="utf-8") == recorder["render"](
        json.loads(record.read_text(encoding="utf-8"))
    )
