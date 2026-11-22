#!/usr/bin/env python3
"""Read-only, source-pinned audit of the 2026-11-22 inbox Git-count RED.

Never invokes a test suite, mutates source, or alters the performance budget.
"""
import argparse
import json
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

SOURCE = "d948e2cdc8aceba5236f621f62d99f3422dfe4fc"
PARENT = "dcac884fc061869572b12a03befc12f11c009f2f"
NAMESPACE = {"v": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
CASES = (0, 220, 2200)
SIX_PATHS = {
    "Client/Modules/ChallengeWorkflow.fs",
    "Client/Modules/ReviewWorkflow.fs",
    "Client/Modules/LedgerStore.fs",
    "Client.Tests/ChallengeWorkflowTests.fs",
    "Client.Tests/ChallengeStateMachinePropertyTests.fs",
    "Client.Tests/Client.Tests.fsproj",
}


def git(*args):
    proc = subprocess.run(("git", *args), capture_output=True, text=True, check=True)
    return proc.stdout


def read_trx(path):
    root = ET.parse(path).getroot()
    counters = root.find(".//v:Counters", NAMESPACE)
    if counters is None:
        raise ValueError(f"No TRX test counters in {path}")
    observed = {}
    for item in root.findall(".//v:UnitTestResult", NAMESPACE):
        name = item.attrib.get("testName", "")
        for count in CASES:
            if name == f"AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness({count})":
                text = "\n".join(
                    (node.text or "") for node in item.findall(".//v:Message", NAMESPACE)
                )
                observed[count] = {
                    "status": item.attrib.get("outcome"),
                    "error": text.strip(),
                    "duration": item.attrib.get("duration"),
                }
    if set(observed) != set(CASES):
        raise ValueError(f"Expected {CASES} in {path}, got {sorted(observed)}")
    return {"counters": counters.attrib, "cases": observed}


def audit_source():
    if git("rev-parse", f"{SOURCE}^").strip() != PARENT:
        raise ValueError("Frozen source no longer matches the asserted parent")
    paths = set(git("diff", "--name-only", PARENT, SOURCE).splitlines())
    if paths != SIX_PATHS:
        raise ValueError(f"Unexpected six-path frozen diff: {sorted(paths)}")
    ledger_new = git("show", f"{SOURCE}:Client/Modules/LedgerStore.fs")
    ledger_old = git("show", f"{PARENT}:Client/Modules/LedgerStore.fs")
    review_new = git("show", f"{SOURCE}:Client/Modules/ReviewWorkflow.fs")
    review_old = git("show", f"{PARENT}:Client/Modules/ReviewWorkflow.fs")
    performance = git("show", f"{SOURCE}:Client.Tests/PerformanceRegression.fs")
    if "let listCompleteWaitIndexesAt" in ledger_old:
        raise ValueError("Parent unexpectedly already has complete wait-index scan")
    if "verifyOwnerWaitIndexesAt binding snapshot agentId" in review_old:
        raise ValueError("Parent unexpectedly already scans owner wait history in inbox")
    assert "verifyOwnerWaitIndexesAt binding snapshot agentId" in review_new
    block = ledger_new.split("let listCompleteWaitIndexesAt", 1)[1].split(
        "// Read-only full-tree evidence", 1
    )[0]
    assert "listAt binding snapshot prefix" in block
    assert '"log"; "--first-parent"; "--diff-filter=A"' in block
    assert 'run binding.RepositoryPath' in block
    assert "measured.ProcessStarts <- measured.ProcessStarts + 1" in ledger_new
    assert "let measureGitReads operation" in ledger_new
    assert "let boundedInbox metric = boundedInboxWithMaxProcesses 12 metric" in performance
    return {
        "source": SOURCE,
        "parent": PARENT,
        "changedPaths": sorted(paths),
        "addedInboxGuard": True,
        "addedEmptyIndexPrefixGitCommands": [
            "ls-tree -r --name-only <pinned-commit> -- challenge-waits/by-owner/<owner>",
            "log --first-parent --diff-filter=A --format= --name-only <pinned-commit> -- challenge-waits/by-owner/<owner>",
        ],
        "measurement": "cumulative Git wrapper invocations, NOT simultaneous OS processes",
        "budgetUnchanged": 12,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--red-trx", type=Path, required=True)
    parser.add_argument("--baseline-trx", type=Path)
    args = parser.parse_args()
    record = {"source": audit_source(), "frozenFull": read_trx(args.red_trx)}
    full = record["frozenFull"]
    assert full["counters"]["passed"] == "1385"
    assert full["counters"]["failed"] == "3"
    for item in full["cases"].values():
        assert item["status"] == "Failed"
        assert "Expected: less than or equal to 12" in item["error"]
        assert "But was:  13" in item["error"]
    if args.baseline_trx is not None:
        baseline = read_trx(args.baseline_trx)
        record["isolatedParentBaseline"] = baseline
        if not all(v["status"] == "Passed" for v in baseline["cases"].values()):
            raise ValueError("Parent control did not pass: do not claim causal delta")
    print(json.dumps(record, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
