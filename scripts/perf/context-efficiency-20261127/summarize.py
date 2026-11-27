#!/usr/bin/env python3
"""Summarize measured registered-route workflow reports; fail closed on semantic drift."""
from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import sys

BYTE_FIELDS = ("RawResultBytes", "SerializedResponseBytes", "DeliveredTextBytes",
               "DeliveredMcpJsonBytes", "TerminalPayloadBytes")
FIXTURE_FIELDS = ("HistoryTasks", "FrontierTasks", "RegisteredAgents",
                  "HistoricalIntegrations", "HistoricalReviews", "FrontierReviews",
                  "FrontierPendingIntegrations", "Representative", "TransportDelayMs", "CeilingOnly")
# Snapshot/commit IDs differ because canonical synthetic transitions use current UTC.
# Keep task, attempt, spec, role, content hash, generation and review identities.
VOLATILE = {"snapshotcommit", "committedhead", "operationid", "lastseenatutc",
            "stateenteredatutc", "workingturnstartedatutc", "expiresatutc",
            "createdatutc", "startedatutc", "recordedatutc", "completedatutc"}

def normalize(value):
    if isinstance(value, dict):
        return {key: normalize(item) for key, item in value.items()
                if key.lower() not in VOLATILE}
    if isinstance(value, list):
        return [normalize(item) for item in value]
    if isinstance(value, str):
        # The prior harness's normalized response projection contains JSON in Result.
        if value.startswith(("{", "[")):
            try:
                return normalize(json.loads(value))
            except json.JSONDecodeError:
                pass
        # F# printed activation receipt carries nondeterministic OIDs and operation IDs.
        value = re.sub(r'(CommittedHead\s*=\s*")([0-9a-f]{40,64})(")', r'\1<commit>\3', value)
        value = re.sub(r'(OperationId\s*=\s*")([0-9a-f-]{36})(")', r'\1<operation>\3', value)
    return value

def terminal_rows(report):
    terminals = {}
    starts = Counter()
    handles = set()
    active = {}
    for row in report["Calls"]:
        name = row["Scenario"]
        pending = row["PendingJobId"]
        terminal = row["TerminalClass"] is not None
        if row["Tool"] != "GetJobResult":
            if row["PolledJobId"] is not None:
                raise ValueError(f"{name}: start incorrectly marked as poll")
            starts[name] += 1
            if starts[name] != 1 or name in terminals:
                raise ValueError(f"{name}: repeated route start")
            if pending is not None:
                if not isinstance(pending, str) or not pending or pending in handles:
                    raise ValueError(f"{name}: invalid/reused job handle")
                if terminal:
                    raise ValueError(f"{name}: initial pending handle also marked terminal")
                handles.add(pending)
                active[name] = pending
            elif not terminal:
                raise ValueError(f"{name}: synchronous start missing terminal")
        else:
            if name not in active:
                raise ValueError(f"{name}: poll without an outstanding handle")
            if row["PolledJobId"] != active[name]:
                raise ValueError(f"{name}: poll handle differs from exact outstanding handle")
            if pending is not None:
                raise ValueError(f"{name}: poll unexpectedly issued a new handle")
            if terminal:
                del active[name]
        if terminal:
            if name in terminals:
                raise ValueError(f"{name}: more than one terminal response")
            terminals[name] = row
    if active or set(starts) != set(terminals):
        raise ValueError("Missing or repeated route start/terminal receipt")
    return terminals

def verify_legacy_workload(report, terminals):
    """Check actual route semantics, not just planned fixture metadata."""
    def need(condition, message):
        if not condition:
            raise ValueError(message)

    def result(name):
        row = terminals[name]
        need(row["TerminalClass"] == "Success", f"{name}: expected success")
        return json.loads(json.loads(row["TerminalProjection"])["Result"])

    cycles = report["Cycles"]
    names = [c["Name"] for c in cycles]
    expected = ["overview"] if report["CeilingOnly"] else [
        "interpreter", "worker-bootstrap", "reviewer-bootstrap", "overview"]
    need(names == expected, "Unexpected or reordered cycles")
    for a, b in zip(cycles, cycles[1:]):
        need(a["AfterHead"] == b["BeforeHead"], "Disconnected ledger head evidence")
    if not report["CeilingOnly"]:
        observations = [n for n in terminals if n.startswith("observe-")]
        need(len(observations) == (12 if report["Representative"] else 4),
             "Wrong observation count")
        for name in observations:
            r = result(name)
            need(r["observerAgentId"] == "Interpreter"
                 and r["targetAgentId"] == name.removeprefix("observe-"),
                 f"{name}: observation identity mismatch")
        inbox = result("worker-inbox")
        task = result("worker-task")
        page = result("worker-spec-page")
        attempt = inbox["Focused"]["Attempt"]
        assignment = inbox["Agent"]["CurrentAssignment"]
        receipt = json.loads(terminals["worker-inbox"]["TerminalProjection"])["InboxActivation"]
        worker_cycle = cycles[1]
        need(isinstance(receipt, str) and all(part in receipt for part in (
            'AgentId = "WorkerReady"', 'ActivatedByThisCall = true',
            'BeforeState = Some "Ready"', 'AfterState = Some "Working"',
            worker_cycle["AfterHead"])), "Worker activation receipt mismatch")
        need(inbox["Agent"]["Execution"] == "Working"
             and assignment["TaskId"] == "frontier-01"
             and assignment["AttemptId"] == attempt["AttemptId"]
             and attempt["OwnerAgentId"] == "WorkerReady"
             and attempt["AssignmentGeneration"] == 1
             and task["attempt"] == attempt
             and task["specBlobOid"] == attempt["SpecBlobOid"] == page["SpecBlobOid"]
             and task["specHtml"] == page["Content"] and not page["HasMore"],
             "Worker assignment/spec drift")
        need(cycles[2]["BeforeHead"] == cycles[2]["AfterHead"],
             "Reviewer read-only bootstrap changed ledger")
        reviews = result("reviewer-inbox")["reviews"]
        need(len(reviews) == 1 and reviews[0]["Status"] == "Unclaimed"
             and reviews[0] == result("review-detail")["Review"]
             and reviews[0] == result("reviewer-details")["Review"],
             "Review demand mismatch")
        pending = result("integration-frontier")["Submissions"]
        need(len(pending) == (1 if report["Representative"] else 0),
             "Pending integration demand mismatch")
        if pending:
            need(pending[0]["TaskId"] == "frontier-04"
                 and pending[0]["Status"] == "Pending",
                 "Frontier integration not actionable")
    overview = terminals["task-overview"]
    if overview["TerminalClass"] == "Success":
        counts = overview["TopLevelCountsAndOmissions"]
        need(counts["Tasks"] == report["HistoryTasks"] + report["FrontierTasks"] + 1
             and counts["Reviews"] == report["HistoricalReviews"] + report["FrontierReviews"],
             "Overview task/review counts differ from fixture")
    # A ceiling failure is valid recorded evidence, not a successful overview.
    # An intentional candidate projection requires its own reviewed adapter.


def load(path):
    report = json.loads(path.read_text())
    if report["SchemaVersion"] != 1 or not re.fullmatch(r"[a-f0-9]{40,64}", report["SourceSha"]):
        raise ValueError("Missing exact measured source or unsupported schema")
    terminals = terminal_rows(report)
    verify_legacy_workload(report, terminals)
    for row in report["Calls"]:
        for field in BYTE_FIELDS:
            if not isinstance(row[field], int) or row[field] < 0:
                raise ValueError(f"{row['Scenario']}: invalid {field}")
        if row["DeliveredMcpJsonBytes"] != len(row["ToolJson"].encode()):
            raise ValueError("Delivered MCP JSON byte count disagrees with captured artifact")
    next_index = 0
    for cycle in report["Cycles"]:
        if cycle["FirstCallIndex"] != next_index or cycle["CallCount"] <= 0:
            raise ValueError("Noncontiguous or empty cycle")
        next_index += cycle["CallCount"]
        changed = cycle["BeforeHead"] != cycle["AfterHead"]
        if cycle["Name"] in ("interpreter", "overview") and changed:
            raise ValueError("Read-only cycle changed ledger head")
        if cycle["Name"] == "worker-bootstrap" and not changed:
            raise ValueError("Expected worker activation to persist")
    if next_index != len(report["Calls"]):
        raise ValueError("Unaccounted calls")
    return report

def summaries(report):
    output = {}
    for cycle in report["Cycles"]:
        start = cycle["FirstCallIndex"]
        rows = report["Calls"][start:start + cycle["CallCount"]]
        segments = [seg for row in rows for seg in row["ContextSegments"]]
        multiplicity = Counter(seg["Sha256"] for seg in segments)
        repeated = 0
        repeated_escaped = 0
        seen = set()
        for seg in segments:
            key = seg["Sha256"]
            if key in seen:
                repeated += seg["ContentBytes"]
                repeated_escaped += seg["EscapedJsonBytes"]
            seen.add(key)
        output[cycle["Name"]] = {
            "calls": len(rows),
            "route_starts": sum(row["Tool"] != "GetJobResult" for row in rows),
            "job_handles": sum(row["PendingJobId"] is not None for row in rows),
            "job_polls": sum(row["Tool"] == "GetJobResult" for row in rows),
            "elapsed_ms": round(cycle["DurationMs"], 2),
            **{field: sum(row[field] for row in rows) for field in BYTE_FIELDS},
            "instruction_spec_content_bytes": sum(seg["ContentBytes"] for seg in segments),
            "instruction_spec_escaped_json_bytes": sum(seg["EscapedJsonBytes"] for seg in segments),
            "duplicate_instruction_spec_content_bytes": repeated,
            "duplicate_instruction_spec_escaped_json_bytes": repeated_escaped,
            "duplicated_segment_hashes": sum(v > 1 for v in multiplicity.values()),
            "terminal_classes": {row["Scenario"]: row["TerminalClass"] for row in rows
                                 if row["TerminalClass"] is not None},
            "terminal_counts_and_omissions": {
                row["Scenario"]: row["TopLevelCountsAndOmissions"] for row in rows
                if row["TerminalClass"] is not None},
        }
    return output

def compare(baseline, candidate):
    for field in FIXTURE_FIELDS:
        if baseline[field] != candidate[field]:
            raise ValueError(f"Different fixture setting {field}")
    left, right = terminal_rows(baseline), terminal_rows(candidate)
    if set(left) != set(right):
        raise ValueError("Workflow route set changed: require reviewed workload-level adapter")
    changed = []
    for name in sorted(left):
        a, b = left[name], right[name]
        # Distinguish documented expected overview truncation from accidental omission.
        if a["TerminalClass"] != b["TerminalClass"]:
            changed.append(f"{name}: terminal class changed")
        if a["TopLevelCountsAndOmissions"] != b["TopLevelCountsAndOmissions"]:
            changed.append(f"{name}: counts/omissions changed")
        if normalize(json.loads(a["TerminalProjection"])) != normalize(json.loads(b["TerminalProjection"])):
            changed.append(f"{name}: normalized full terminal semantics changed")
    return changed

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", nargs="?", type=Path)
    args = parser.parse_args()
    baseline = load(args.baseline)
    result = {"source": baseline["SourceSha"], "cycles": summaries(baseline)}
    if args.candidate:
        candidate = load(args.candidate)
        result["candidate_source"] = candidate["SourceSha"]
        result["candidate_cycles"] = summaries(candidate)
        result["semantic_differences"] = compare(baseline, candidate)
    print(json.dumps(result, indent=2))
    return int(bool(result.get("semantic_differences")))

if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, KeyError, TypeError) as error:
        print(f"Invalid evidence: {error}", file=sys.stderr)
        sys.exit(2)
