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

TRX_NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
COUNTER_KEYS = ("total", "executed", "passed", "failed", "notExecuted")
FAILURE_COUNTERS = ("error", "timeout", "aborted", "inconclusive",
                    "passedButRunAborted", "notRunnable", "disconnected",
                    "warning", "inProgress", "pending")


def release_invocation(argv):
    """Require exactly one documented Release switch before test-runner arguments."""
    if not isinstance(argv, list) or not all(isinstance(a, str) for a in argv):
        return False
    if argv[:2] != ["dotnet", "test"]:
        return False
    before_runner = argv[:argv.index("--")] if "--" in argv else argv
    if before_runner.count("Client.Tests/Client.Tests.fsproj") != 1:
        return False
    if any(arg == "--filter" or arg.startswith(("--filter=", "--filter:"))
           for arg in argv):
        return False
    configurations = []
    index = 2
    while index < len(before_runner):
        arg = before_runner[index]
        if arg in ("-c", "--configuration"):
            index += 1
            if index >= len(before_runner) or before_runner[index] != "Release":
                return False
            configurations.append(before_runner[index])
        elif arg.startswith("--configuration="):
            configurations.append(arg.partition("=")[2])
        elif (arg.lower().startswith(("-c=", "-c:", "--config",
                                      "-p:configuration", "/p:configuration",
                                      "-property:configuration", "/property:configuration",
                                      "--property:configuration"))
              or (arg.lower() in ("-p", "/p", "-property", "/property", "--property")
                  and index + 1 < len(before_runner)
                  and before_runner[index + 1].lower().startswith("configuration"))):
            # Unsupported/ambiguous forms cannot silently override Release.
            return False
        index += 1
    return configurations == ["Release"]


def parse_trx(path):
    root = ET.parse(path).getroot()
    ns = {"t": TRX_NS}
    if root.tag != f"{{{TRX_NS}}}TestRun":
        raise ValueError("unexpected TRX root")
    result_nodes = root.findall("t:Results", ns)
    summaries = root.findall("t:ResultSummary", ns)
    if len(result_nodes) != 1 or len(summaries) != 1:
        raise ValueError("missing or duplicated Results/ResultSummary")
    results = result_nodes[0].findall("t:UnitTestResult", ns)
    if not results:
        raise ValueError("TRX contains no test outcomes")
    outcome = summaries[0].attrib.get("outcome")
    if outcome not in ("Completed", "Passed", "Failed"):
        raise ValueError("nonterminal or absent summary outcome")
    counters = summaries[0].findall("t:Counters", ns)
    if len(counters) != 1:
        raise ValueError("missing or duplicated summary Counters")
    declared = counters[0].attrib
    if any(key not in declared for key in COUNTER_KEYS):
        raise ValueError("missing required TRX counter")
    try:
        counts = {key: int(value) for key, value in declared.items()}
    except ValueError as exc:
        raise ValueError("nonnumeric TRX counter") from exc
    if any(value < 0 for value in counts.values()):
        raise ValueError("negative TRX counter")
    cases = []
    for entry in results:
        status = entry.attrib.get("outcome", "").lower()
        name = entry.attrib.get("testName", "")
        messages = [e.text or "" for e in entry.findall(".//t:Message", ns)]
        stack = [e.text or "" for e in entry.findall(".//t:StackTrace", ns)]
        cases.append({"name": name, "outcome": status,
                      "failure_message": " ".join(messages),
                      "stack": " ".join(stack)})
    passed = sum(c["outcome"] == "passed" for c in cases)
    skipped = sum(c["outcome"] in ("notexecuted", "skipped") for c in cases)
    failed = [c for c in cases if c["outcome"] not in
              ("passed", "notexecuted", "skipped")]
    # VSTest/NUnit can emit NotExecuted UnitTestResult for skipped tests
    # while the TRX notExecuted counter stays zero. The observable skipped
    # count is total - executed; notExecuted is either zero or that count.
    if (counts["total"] != len(cases)
            or counts["executed"] != passed + len(failed)
            or counts["passed"] != passed
            or counts["failed"] != len(failed)
            or len(cases) != counts["executed"] + skipped
            or counts["notExecuted"] not in (0, skipped)
            or ("skipped" in counts and counts["skipped"] not in (0, skipped))):
        raise ValueError("summary counters contradict individual outcomes")
    if any(counts.get(key, 0) != 0 for key in FAILURE_COUNTERS) and not failed:
        raise ValueError("unexpected nonzero failure/status counters")
    if outcome == "Failed" and not failed:
        raise ValueError("failed summary contradicts passing results")
    if outcome == "Passed" and failed:
        raise ValueError("passing summary contradicts failed results")
    return {"result_count": len(cases), "declared_counters": declared,
            "summary_outcome": outcome, "failed": failed,
            "passed": passed, "skipped": skipped}

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
    if not release_invocation(argv) or manifest.get("test_filter") is not None:
        return {"verdict": "UNKNOWN",
                "reason": "not an unfiltered, unambiguous dotnet test Release invocation"}
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
    try:
        parsed = [parse_trx(p) for p in paths]
    except (ET.ParseError, ValueError, OSError) as exc:
        return {**base, "verdict": "UNKNOWN",
                "reason": f"invalid or inconsistent TRX summary/counters: {exc}"}
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
    elif manifest["exit_code"] == 0 and not failed and all(
            item["summary_outcome"] in ("Completed", "Passed") for item in parsed):
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
