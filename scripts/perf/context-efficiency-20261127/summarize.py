#!/usr/bin/env python3
"""Summarize measured registered-route workflow reports; fail closed on semantic drift."""
from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import sys
import subprocess
from run_evidence import clean_sha, digest, DIRECTORY, PROJECT

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
    return value


OID = r"(?:[0-9a-f]{40}|[0-9a-f]{64})"
UUID = r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
ACTIVATION = re.compile(
    r'\{\s*ProjectName = "FixtureProject"\s+AgentId = "WorkerReady"\s+'
    r'ActivatedByThisCall = true\s+BeforeState = Some "Ready"\s+'
    r'AfterState = Some "Working"\s+CommittedHead = Some "(?P<head>' + OID +
    r')"\s+OperationId = Some "(?P<operation>' + UUID + r')"\s*\}'
)

def activation_projection(report, projection):
    """Parse the actual F# option receipt before masking its two per-run IDs."""
    match = ACTIVATION.fullmatch(projection["InboxActivation"] or "")
    if not match:
        raise ValueError("Malformed worker activation receipt")
    inbox = json.loads(projection["Result"])
    head = next(c["AfterHead"] for c in report["Cycles"] if c["Name"] == "worker-bootstrap")
    if match["head"] != head or inbox["SnapshotCommit"] != head:
        raise ValueError("Worker activation head does not correlate with inbox and ledger")
    if match["operation"] == "00000000-0000-0000-0000-000000000000":
        raise ValueError("Empty worker activation operation ID")
    # Keep every validated stable field (and the option shape).
    receipt = projection["InboxActivation"]
    for field in ("head", "operation"):
        receipt = receipt.replace(match[field], "<" + field + ">", 1)
    return {**projection, "InboxActivation": receipt}


def expected_routes(report):
    """Explicit route matrix matching Program.fs, independently of captured rows."""
    def routes(actor, pairs):
        return [(name, tool, actor) for name, tool in pairs]

    overview = ("overview", routes("Interpreter", [("task-overview", "TaskOverview")]))
    if report["CeilingOnly"]:
        return [overview]
    targets = ["WorkerReady", "WorkerBusy", "WorkerIdle", "Reviewer"]
    if report["Representative"]:
        targets += [f"FrontierObserver{i:02d}" for i in range(1, 9)]
    interpreter = [("observe-" + target, "ObserveAgent") for target in targets]
    interpreter += [(f"task-frontier-{i:02d}", "GetTask") for i in range(1, 4)]
    interpreter += [("review-detail", "ReviewDetails"),
                    ("integrations-history", "GetIntegrationSubmissions"),
                    ("integration-frontier", "GetIntegrationSubmissions")]
    return [
        ("interpreter", routes("Interpreter", interpreter)),
        ("worker-bootstrap", routes("WorkerReady", [
            ("worker-inbox", "AgentInbox"), ("worker-instructions", "GetAgentInstructions"),
            ("worker-task", "GetTask"), ("worker-spec-page", "GetTaskSpecPage")])),
        ("reviewer-bootstrap", routes("Reviewer", [
            ("reviewer-inbox", "ReviewerInbox"), ("reviewer-instructions", "GetAgentInstructions"),
            ("reviewer-task", "GetTask"), ("reviewer-details", "ReviewDetails")])),
        overview,
    ]


def validate_terminal(report, row):
    name, kind = row["Scenario"], row["TerminalClass"]
    projection = json.loads(row["TerminalProjection"])
    if projection["PendingJob"] is not None:
        raise ValueError(f"{name}: terminal has pending job")
    if name != "worker-inbox" and projection["InboxActivation"] is not None:
        raise ValueError(f"{name}: unexpected activation receipt")
    if kind == "Success":
        if projection["Error"] is not None or projection["Result"] is None:
            raise ValueError(f"{name}: inconsistent success projection")
        if name == "integrations-history" and report["HistoryTasks"] == 0:
            raise ValueError(f"{name}: expected missing historical task")
        return
    if projection["Error"] != kind or projection["Result"] is not None:
        raise ValueError(f"{name}: inconsistent error projection")
    if name == "task-overview":
        match = re.fullmatch(r'OutputTruncated\s+"Command response was (\d+) bytes and exceeds the safe response size of (\d+) bytes\."', kind)
        if match and int(match[1]) > int(match[2]) == 921600:
            return
    if name == "integrations-history" and report["HistoryTasks"] == 0:
        if (kind.startswith("LedgerFailureError") and 'Code = "StaleEntityVersion"' in kind
                and "Missing ledger entity: tasks/history00000/task.json" in kind):
            return
    raise ValueError(f"{name}: unexpected terminal {kind}")


def validate_route_matrix(report):
    expected = expected_routes(report)
    cycles = report["Cycles"]
    if [c["Name"] for c in cycles] != [name for name, _ in expected]:
        raise ValueError("Unexpected or reordered cycles")
    next_index = 0
    for cycle, (cycle_name, routes) in zip(cycles, expected):
        first, count = cycle["FirstCallIndex"], cycle["CallCount"]
        if first != next_index or count <= 0 or first + count > len(report["Calls"]):
            raise ValueError(f"{cycle_name}: invalid cycle boundary")
        rows = report["Calls"][first:first + count]
        cursor = 0
        for name, tool, actor in routes:
            if cursor >= len(rows):
                raise ValueError(f"{cycle_name}: missing route {name}")
            row = rows[cursor]
            if (row["Scenario"], row["Tool"], row["Actor"]) != (name, tool, actor):
                raise ValueError(f"{cycle_name}: expected {name}/{tool}/{actor}")
            cursor += 1
            while row["TerminalClass"] is None:
                if cursor >= len(rows):
                    raise ValueError(f"{name}: missing terminal in cycle")
                row = rows[cursor]
                if (row["Scenario"], row["Tool"], row["Actor"]) != (name, "GetJobResult", actor):
                    raise ValueError(f"{name}: unexpected poll or interleaved route")
                cursor += 1
            validate_terminal(report, row)
        if cursor != len(rows):
            raise ValueError(f"{cycle_name}: extra route or poll")
        next_index += count
    if next_index != len(report["Calls"]):
        raise ValueError("Unaccounted calls")


def terminal_rows(report):
    validate_route_matrix(report)
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
        activation_projection(report, json.loads(terminals["worker-inbox"]["TerminalProjection"]))
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
    verify_legacy_workload(baseline, left)
    verify_legacy_workload(candidate, right)
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
        ap, bp = json.loads(a["TerminalProjection"]), json.loads(b["TerminalProjection"])
        if name == "worker-inbox":
            ap, bp = activation_projection(baseline, ap), activation_projection(candidate, bp)
        if normalize(ap) != normalize(bp):
            changed.append(f"{name}: normalized full terminal semantics changed")
    return changed


def provenance(path, report):
    """Legacy/direct runs are analyzable but explicitly not provenance-bound."""
    path = path.resolve()
    receipt_path = path.with_name(path.name + ".receipt.json")
    if not receipt_path.is_file():
        return {"status": "unqualified", "reason": "No controlled build/run receipt"}
    try:
        receipt = json.loads(receipt_path.read_text())
        def need(condition, reason):
            if not condition:
                raise ValueError(reason)
        root = path.parent
        runtime, harness, output = root / "runtime", root / "harness", root / "build"
        project = harness / DIRECTORY / PROJECT
        refs = [runtime / "Client/Client.fsproj", runtime / "Server/Server.fsproj"]
        need(receipt["schema"] == 1 and receipt["status"] == "bound",
             "Incomplete controlled invocation")
        need(receipt["artifact_sha256"] == digest(path), "Report digest mismatch")
        need(report["SourceSha"] == receipt["source_sha"]
             and report["SourceRoot"] == receipt["runtime_root"] == str(runtime)
             and receipt["harness_root"] == str(harness),
             "Source or snapshot path mismatch")
        need(clean_sha(runtime) == receipt["source_sha"]
             and clean_sha(harness) == receipt["harness_sha"], "Snapshot revision mismatch")
        need(receipt["project"] == str(project)
             and receipt["references"] == [str(ref) for ref in refs],
             "Reference paths do not bind the measured runtime")
        expected_inputs = (PROJECT, "Program.fs", "summarize.py", "run_evidence.py")
        need(receipt["harness_inputs"] == {name: digest(project.parent / name)
                                          for name in expected_inputs},
             "Harness input digest mismatch")
        commands = receipt["commands"]
        labels = ["runtime-clone", "runtime-checkout", "harness-clone", "harness-checkout",
                  "sdk", "build", "run"]
        need([entry["label"] for entry in commands] == labels, "Incomplete invocation sequence")
        for entry in commands:
            log = root / (entry["label"] + ".log")
            need(entry["exit_code"] == 0 and entry["log"] == str(log)
                 and entry["log_sha256"] == digest(log), "Invalid command/log receipt")
        build, run = commands[-2:]
        dll = output / "ContextEfficiency.Harness.dll"
        expected_build = ["dotnet", "build", str(project), "-c", "Release", "--output",
                          str(output), "-p:JarvisClientProject=" + str(refs[0]),
                          "-p:JarvisServerProject=" + str(refs[1])]
        expected_run = ["dotnet", str(dll), "--source-root", str(runtime), "--history",
                        str(report["HistoryTasks"]), "--transport-delay-ms",
                        str(report["TransportDelayMs"]), "--out", str(path)]
        if report["Representative"]:
            expected_run.append("--representative")
        if report["CeilingOnly"]:
            expected_run.append("--ceiling-only")
        need(build["argv"] == expected_build and run["argv"] == expected_run
             and build["cwd"] == run["cwd"] == str(runtime), "Build/run command mismatch")
        need(dll.is_file() and receipt["build_files"] == {
            str(f.relative_to(output)): digest(f)
            for f in sorted(output.rglob("*")) if f.is_file()}, "Build output digest mismatch")
        return {"status": "bound", "source_sha": receipt["source_sha"],
                "harness_sha": receipt["harness_sha"], "artifact_sha256": receipt["artifact_sha256"],
                "job_threshold_ms": receipt["job_threshold_ms"]}
    except (ValueError, KeyError, TypeError, OSError, RuntimeError, subprocess.SubprocessError) as error:
        return {"status": "unqualified", "reason": str(error)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", nargs="?", type=Path)
    args = parser.parse_args()
    baseline = load(args.baseline)
    result = {"source": baseline["SourceSha"], "cycles": summaries(baseline),
              "provenance": provenance(args.baseline, baseline)}
    if args.candidate:
        candidate = load(args.candidate)
        result["candidate_source"] = candidate["SourceSha"]
        result["candidate_cycles"] = summaries(candidate)
        result["candidate_provenance"] = provenance(args.candidate, candidate)
        result["semantic_differences"] = compare(baseline, candidate)
        left, right = result["provenance"], result["candidate_provenance"]
        result["comparison_provenance_bound"] = (
            left["status"] == right["status"] == "bound"
            and left["harness_sha"] == right["harness_sha"]
            and left["job_threshold_ms"] == right["job_threshold_ms"])
        if not result["comparison_provenance_bound"]:
            result["comparison_limit"] = "Diagnostic only: missing/mismatched invocation provenance or thresholds"
    print(json.dumps(result, indent=2))
    return int(bool(result.get("semantic_differences")))

if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, KeyError, TypeError) as error:
        print(f"Invalid evidence: {error}", file=sys.stderr)
        sys.exit(2)
