"""Mutation probes for policy checked against compiler declarations."""

import copy
import json
import pathlib
import runpy

import pytest

ROOT = pathlib.Path(__file__).parents[3]
VALIDATE = runpy.run_path(str(ROOT / "eng/parity/verify_public_api.py"))["validate"]


@pytest.fixture(scope="module")
def documents():
    return tuple(json.loads((ROOT / path).read_text()) for path in (
        "docs/public-api.json", "docs/parity/parity-ledger.json", "artifacts/api-inventory.json"
    ))


def test_compiler_symbols_satisfy_policy(documents):
    assert VALIDATE(*documents) == []


@pytest.mark.parametrize("mutation,expected", [
    ("unknown", "unknown approved destination"),
    ("visibility", "destination visibility disagrees"),
    ("ownership", "borrowed type is disposable"),
    ("cancellation", "invalid cancellation parameter"),
    ("platform", "missing Windows annotation"),
])
def test_compiler_policy_rejects_broken_contract(documents, mutation, expected):
    policy, ledger, inventory = copy.deepcopy(documents)
    symbols = {m["id"]: m for m in inventory["members"]}
    if mutation in {"unknown", "visibility"}:
        row = next(r for r in ledger["rows"] if r["destinationStatus"] == "approved")
        if mutation == "unknown":
            row["csharpDestination"] = "M:LibTmux.Missing"
        else:
            symbols[row["csharpDestination"]]["visibility"] = "internal"
    elif mutation == "ownership":
        symbols["T:LibTmux.Server"]["interfaces"].append("System.IAsyncDisposable")
    elif mutation == "cancellation":
        rule = next(r for r in policy["members"] if r.get("performsIO"))
        symbols[rule["id"]]["parameters"][-1]["optional"] = False
    else:
        rule = next(r for r in policy["members"] if r.get("processBacked") and not r.get("portable") and symbols[r["id"]]["visibility"] == "public")
        member = symbols[rule["id"]]
        member["attributes"] = []
        symbols[member["declaringType"]]["attributes"] = []
    assert any(expected in violation for violation in VALIDATE(policy, ledger, inventory))
