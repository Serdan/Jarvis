#!/usr/bin/env python3
"""Fail-closed TRX + process evidence classifier for one real Jarvis Client Release.
Never interprets STDERR permission negatives as failing tests without TRX.
"""
import argparse
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

SOURCE = "dcac884fc061869572b12a03befc12f11c009f2f"
SDK = "11.0.100"

def parse_trx(path):
    root = ET.parse(path).getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    results = root.findall(".//t:UnitTestResult", ns)
    cases = []
    for entry in results:
        outcome = entry.attrib.get("outcome", "Unknown")
        name = entry.attrib.get("testName", "")
        messages = [e.text or "" for e in entry.findall(".//t:Message", ns)]
        stack = [e.text or "" for e in entry.findall(".//t:StackTrace", ns)]
        cases.append({"name": name, "outcome": outcome,
                      "failure_message": " ".join(messages),
                      "stack": " ".join(stack)})
    counters = root.find(".//t:Counters", ns)
    declared = counters.attrib if counters is not None else {}
    return {"result_count": len(cases), "declared_counters": declared,
            "failed": [c for c in cases if c["outcome"].lower() not in
                       ("passed", "notexecuted", "skipped")],
            "passed": sum(c["outcome"].lower() == "passed" for c in cases),
            "skipped": sum(c["outcome"].lower() in ("notexecuted", "skipped") for c in cases)}

def classify(folder):
    manifest_path = folder / "manifest.json"
    if not manifest_path.is_file():
        return {"verdict": "UNKNOWN", "reason": "missing manifest"}
    manifest = json.loads(manifest_path.read_text())
    if manifest.get("source") != SOURCE or manifest.get("source_after") != SOURCE:
        return {"verdict": "UNKNOWN", "reason": "source provenance mismatch"}
    if manifest.get("sdk") != SDK:
        return {"verdict": "UNKNOWN", "reason": "SDK provenance mismatch"}
    argv = manifest.get("cmd", [])
    if not isinstance(argv, list) or argv[:2] != ["dotnet", "test"] or \
       "Client.Tests/Client.Tests.fsproj" not in argv or \
       manifest.get("test_filter") is not None or \
       any(part.startswith("--filter") for part in argv):
        return {"verdict": "UNKNOWN", "reason": "not an unfiltered Client.Tests invocation"}
    if "exit_code" not in manifest:
        return {"verdict": "UNKNOWN", "reason": "job has no terminal exit"}
    terminal = folder / "terminal.log"
    activity_count = (len(re.findall(
        r"Jarvis activity persistence: .+ failed \((?:UnauthorizedAccessException|InvalidDataException|IOException)\)",
        terminal.read_text(errors="replace"))) if terminal.is_file() else 0)
    paths = [Path(x) for x in manifest.get("trx_paths", [])]
    base = {"source": SOURCE, "sdk": SDK, "exit_code": manifest["exit_code"],
            "timed_out": bool(manifest.get("timed_out", False)),
            "trx_paths": [str(p) for p in paths],
            "process_summary": manifest.get("process_summary", {}),
            "activity_negative_logs": activity_count}
    # A child can exit 0 after SIGTERM despite the launcher reporting 124.
    # No TRX, or a partial TRX after a timeout, is never a complete PASS/RED.
    if base["timed_out"]:
        return {**base, "verdict": "UNKNOWN", "reason": "bounded timeout; no complete test verdict"}
    if not paths or not all(p.is_file() for p in paths):
        return {**base, "verdict": "UNKNOWN", "reason": "terminal without complete TRX"}
    parsed = [parse_trx(p) for p in paths]
    if not all(item["result_count"] for item in parsed):
        return {"verdict": "UNKNOWN", "reason": "TRX contains no test outcomes"}
    failed = [c for item in parsed for c in item["failed"]]
    passed = sum(item["passed"] for item in parsed)
    skipped = sum(item["skipped"] for item in parsed)
    report = {
        **base,
        "passed": passed, "skipped": skipped, "failed_count": len(failed),
        "failed": failed,
        "git_process_contract_failures": []
    }
    report["git_process_contract_failures"] = [
        c["name"] for c in failed
        if "Git subprocesses per historical record" in c["failure_message"]
        or ("AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness" in c["name"]
            and ("12" in c["failure_message"] or "GitProcesses" in c["failure_message"]))
    ]
    if manifest.get("timed_out"):
        report.update({"verdict": "UNKNOWN", "reason": "bounded timeout"})
    elif manifest["exit_code"] == 0 and not failed:
        report.update({"verdict": "PASS", "reason": "terminal exit zero and complete TRX"})
    else:
        report.update({"verdict": "RED", "reason": "test failures or nonzero terminal exit"})
    return report

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("evidence_dir")
    args = parser.parse_args()
    report = classify(Path(args.evidence_dir))
    print(json.dumps(report, indent=2))
    return 0 if report["verdict"] == "PASS" else 1

if __name__ == "__main__":
    raise SystemExit(main())
