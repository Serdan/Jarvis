#!/usr/bin/env python3
"""Compare two Git-call harness reports without weakening semantic or state checks."""

from __future__ import annotations

import argparse
import json
import math
import statistics
import sys
from pathlib import Path


def load(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as stream:
        return json.load(stream)


def by_scenario(report: dict) -> dict[str, dict]:
    return {item["Scenario"]: item for item in report.get("Measurements", [])}


def percentile(values: list[float], fraction: float) -> float | None:
    ordered = sorted(values)
    if not ordered:
        return None
    return ordered[max(0, math.ceil(fraction * len(ordered)) - 1)]


def format_ms(value: float | None) -> str:
    return "n/a" if value is None else f"{value:.2f}"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", type=Path)
    args = parser.parse_args()

    baseline = load(args.baseline)
    candidate = load(args.candidate)
    left = by_scenario(baseline)
    right = by_scenario(candidate)
    names = sorted(set(left) | set(right))
    failed: list[str] = []

    expected = {
        "list-worktrees",
        "get-worktree",
        "get-task",
        "register-agent",
        "get-operation-status",
        "retry-same-operation",
        "operation-id-reused-with-changed-input",
        "unauthorized-register-agent",
        "read-file-worktree-cold",
        "read-file-after-head-update",
        "read-file-after-worktree-add-cold",
        "read-file-after-worktree-add-warm",
    } | {f"read-file-worktree-warm-{index:02d}" for index in range(1, 31)}

    if baseline.get("SourceSha") == candidate.get("SourceSha"):
        failed.append("baseline and candidate reports identify the same source SHA")

    for label, report in (("baseline", baseline), ("candidate", candidate)):
        if report.get("SchemaVersion") != 2:
            failed.append(f"{label}: unsupported or missing report schema version")
        if not report.get("SourceSha"):
            failed.append(f"{label}: exact measured source SHA was not captured")
        if not report.get("MeasuredSourceClean", False):
            failed.append(f"{label}: measured Client/Server source was not clean")
        if report.get("FixtureSeedSha") is None or report.get("FixturePublishedSha") is None:
            failed.append(f"{label}: fixture seed/published source identities were not captured")
        elif report.get("FixtureSeedSha") == report.get("FixturePublishedSha"):
            failed.append(f"{label}: second fixture clone did not publish a new head")
        actual = set(by_scenario(report))
        if actual != expected:
            missing = sorted(expected - actual)
            extra = sorted(actual - expected)
            if missing:
                failed.append(f"{label}: missing required scenarios {missing}")
            if extra:
                failed.append(f"{label}: unexpected scenarios {extra}")

        samples = by_scenario(report)
        cold = samples.get("read-file-worktree-cold")
        if cold is not None:
            for index in range(1, 31):
                warm = samples.get(f"read-file-worktree-warm-{index:02d}")
                if warm is not None and warm.get("ResponseDigest") != cold.get("ResponseDigest"):
                    failed.append(f"{label}: warm read {index} returned stale/different content")
            after_head = samples.get("read-file-after-head-update")
            if after_head is not None and after_head.get("ResponseDigest") == cold.get("ResponseDigest"):
                failed.append(f"{label}: read after published head returned stale content")
            after_add_cold = samples.get("read-file-after-worktree-add-cold")
            after_add_warm = samples.get("read-file-after-worktree-add-warm")
            if after_head is not None and after_add_cold is not None:
                if after_add_cold.get("ResponseDigest") != after_head.get("ResponseDigest"):
                    failed.append(f"{label}: newly registered worktree read returned different content")
            if after_head is not None and after_add_warm is not None:
                if after_add_warm.get("ResponseDigest") != after_head.get("ResponseDigest"):
                    failed.append(f"{label}: warm read from newly registered worktree returned different content")

        for measurement in report.get("Measurements", []):
            scenario = measurement.get("Scenario", "<unknown>")
            if measurement.get("Exception") is not None:
                failed.append(f"{label} {scenario}: harness raised {measurement['Exception']}")
            if measurement.get("ToolResultDigest") is None:
                failed.append(f"{label} {scenario}: MCP tool result was not captured")
            if measurement.get("ToolIsError") is None:
                failed.append(f"{label} {scenario}: MCP error classification was not captured")
            if scenario == "unauthorized-register-agent":
                if measurement.get("ResponseDigest") is not None or measurement.get("ResponseClass") is not None:
                    failed.append(f"{label} {scenario}: unauthorized write reached client dispatch")
                if measurement.get("ToolIsError") is not True:
                    failed.append(f"{label} {scenario}: missing OAuth denial result")
            elif measurement.get("ResponseDigest") is None or measurement.get("ResponseClass") is None:
                failed.append(f"{label} {scenario}: correlated client response was not captured")

    if baseline.get("FixtureSeedSha") != candidate.get("FixtureSeedSha"):
        failed.append("baseline and candidate did not use the same deterministic fixture seed")
    if baseline.get("FixturePublishedSha") != candidate.get("FixturePublishedSha"):
        failed.append("baseline and candidate did not use the same deterministic published fixture head")

    print(
        f"baseline={baseline.get('SourceSha')} "
        f"candidate={candidate.get('SourceSha')}"
    )
    print(
        f"{'scenario':42} {'git':>9} {'fetch':>9} {'total ms':>17} "
        f"{'semantic':>10} {'state':>10}"
    )
    print("-" * 100)

    for name in names:
        a = left.get(name)
        b = right.get(name)
        if a is None or b is None:
            failed.append(f"{name}: scenario missing from one report")
            print(f"{name:42} MISSING")
            continue

        semantic_equal = (
            a.get("ResponseClass") == b.get("ResponseClass")
            and a.get("ResponseDigest") == b.get("ResponseDigest")
            and a.get("ToolResultDigest") == b.get("ToolResultDigest")
            and a.get("ToolIsError") == b.get("ToolIsError")
        )
        state_equal = (
            a.get("RefsBefore") == b.get("RefsBefore")
            and a.get("RefsAfter") == b.get("RefsAfter")
            and a.get("StateDigestBefore") == b.get("StateDigestBefore")
            and a.get("StateDigestAfter") == b.get("StateDigestAfter")
        )
        semantic_label = "MATCH" if semantic_equal else "DIFF"
        state_label = "MATCH" if state_equal else "DIFF"
        git_delta = b.get("GitProcessStarts", 0) - a.get("GitProcessStarts", 0)
        fetch_delta = b.get("RemoteFetches", 0) - a.get("RemoteFetches", 0)
        time_delta = b.get("DurationMilliseconds", 0.0) - a.get("DurationMilliseconds", 0.0)
        print(
            f"{name:42} "
            f"{a.get('GitProcessStarts', 0):>3}->{b.get('GitProcessStarts', 0):<3} "
            f"{a.get('RemoteFetches', 0):>3}->{b.get('RemoteFetches', 0):<3} "
            f"{a.get('DurationMilliseconds', 0.0):>7.2f}->{b.get('DurationMilliseconds', 0.0):<7.2f} "
            f"{semantic_label:>10} {state_label:>10}"
        )
        if a.get("QueueMilliseconds") is not None or b.get("QueueMilliseconds") is not None:
            print(
                f"  dispatch queue ms: {format_ms(a.get('QueueMilliseconds'))}"
                f" -> {format_ms(b.get('QueueMilliseconds'))}; execution ms: "
                f"{format_ms(a.get('ExecutionMilliseconds'))}"
                f" -> {format_ms(b.get('ExecutionMilliseconds'))}"
            )
        command_counts = b.get("CommandCounts") or {}
        observed_commands = [f"{key}={value}" for key, value in sorted(command_counts.items()) if value]
        if observed_commands:
            print(f"  candidate Git commands: {', '.join(observed_commands)}")

        if not semantic_equal:
            failed.append(f"{name}: response/result semantic digest changed")
        if not state_equal:
            failed.append(f"{name}: canonical refs or durable-state digest changed")
        if git_delta > 0:
            print(f"  note: Git process starts increased by {git_delta}")
        if fetch_delta > 0:
            print(f"  note: remote fetches increased by {fetch_delta}")
        if time_delta > 0:
            print(f"  note: end-to-end time increased by {time_delta:.2f} ms (informational)")

        if name == "register-agent":
            for label, measurement in (("baseline", a), ("candidate", b)):
                if measurement.get("StateDigestBefore") is None or measurement.get("StateDigestAfter") is None:
                    failed.append(f"{label} register-agent: durable state was not captured")
                elif measurement.get("StateDigestBefore") == measurement.get("StateDigestAfter"):
                    failed.append(f"{label} register-agent: fixture did not persist the write")
        elif name == "retry-same-operation":
            for label, measurement in (("baseline", a), ("candidate", b)):
                if measurement.get("StateDigestBefore") is None or measurement.get("StateDigestAfter") is None:
                    failed.append(f"{label} retry-same-operation: durable state was not captured")
                elif measurement.get("StateDigestBefore") != measurement.get("StateDigestAfter"):
                    failed.append(f"{label} retry-same-operation: retry changed durable state")
                if measurement.get("RefsBefore") != measurement.get("RefsAfter"):
                    failed.append(f"{label} retry-same-operation: retry changed canonical refs")
        elif name == "operation-id-reused-with-changed-input":
            for label, measurement in (("baseline", a), ("candidate", b)):
                if measurement.get("StateDigestBefore") is None or measurement.get("StateDigestAfter") is None:
                    failed.append(f"{label} changed operation-ID input: durable state was not captured")
                elif measurement.get("StateDigestBefore") != measurement.get("StateDigestAfter"):
                    failed.append(f"{label} changed operation-ID input altered durable state")
                if measurement.get("RefsBefore") != measurement.get("RefsAfter"):
                    failed.append(f"{label} changed operation-ID input altered canonical refs")
            if "OperationIdReused" not in str(a.get("ResponseClass")):
                failed.append("baseline did not preserve OperationIdReused error class")
            if "OperationIdReused" not in str(b.get("ResponseClass")):
                failed.append("candidate did not preserve OperationIdReused error class")
        elif name == "unauthorized-register-agent":
            for label, measurement in (("baseline", a), ("candidate", b)):
                if measurement.get("GitProcessStarts", 0) != 0 or measurement.get("RemoteFetches", 0) != 0:
                    failed.append(f"{label} unauthorized write performed Git work")
                if measurement.get("ResponseDigest") is not None:
                    failed.append(f"{label} unauthorized write reached the client")
                if measurement.get("StateDigestBefore") is None or measurement.get("StateDigestAfter") is None:
                    failed.append(f"{label} unauthorized write: durable state was not captured")
                elif measurement.get("StateDigestBefore") != measurement.get("StateDigestAfter"):
                    failed.append(f"{label} unauthorized write changed durable state")
                if measurement.get("RefsBefore") != measurement.get("RefsAfter"):
                    failed.append(f"{label} unauthorized write changed canonical refs")

    if failed:
        print("\nFAILED:")
        for item in failed:
            print(f"- {item}")
        return 1

    for label, report in (("baseline", baseline), ("candidate", candidate)):
        warm_measurements = [
            item
            for item in report.get("Measurements", [])
            if item.get("Scenario", "").startswith("read-file-worktree-warm-")
        ]
        if warm_measurements:
            total = [item["DurationMilliseconds"] for item in warm_measurements]
            queue = [item["QueueMilliseconds"] for item in warm_measurements if item.get("QueueMilliseconds") is not None]
            execution = [item["ExecutionMilliseconds"] for item in warm_measurements if item.get("ExecutionMilliseconds") is not None]
            print(
                f"{label} warm ReadFile: n={len(total)} "
                f"total p50/p95/p99={statistics.median(total):.2f}/"
                f"{format_ms(percentile(total, 0.95))}/{format_ms(percentile(total, 0.99))}ms "
                f"queue p50/p95/p99={format_ms(statistics.median(queue) if queue else None)}/"
                f"{format_ms(percentile(queue, 0.95))}/{format_ms(percentile(queue, 0.99))}ms "
                f"execution p50/p95/p99={format_ms(statistics.median(execution) if execution else None)}/"
                f"{format_ms(percentile(execution, 0.95))}/{format_ms(percentile(execution, 0.99))}ms "
                f"GitStarts={sum(item.get('GitProcessStarts', 0) for item in warm_measurements)} "
                f"Fetches={sum(item.get('RemoteFetches', 0) for item in warm_measurements)}"
            )

    print("\nSemantic and durable-state equivalence checks passed.")
    print("Latency is informational; process/fetch deltas require lane-level interpretation.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
