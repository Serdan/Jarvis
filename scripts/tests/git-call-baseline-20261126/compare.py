#!/usr/bin/env python3
"""Run the same isolated Git-call harness against two exact Jarvis source commits."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

BASELINE_SHA = "9dc95184896118c96f34493da28ce40bf5b3d8d6"
HARNESS_FILE = Path("Client.Tests/GitCallBaselineTests.fs")
PROJECT_FILE = Path("Client.Tests/Client.Tests.fsproj")
COMPILE_MARKER = "Program.fs"
COMPILE_ITEM = "GitCallBaselineTests.fs"

FOCUSED_FILTERS = [
    "FullyQualifiedName~TaskOverviewBatchPerformanceTests",
    "FullyQualifiedName~AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness",
    "FullyQualifiedName~AgentInboxBatchRejectsMalformedCanonicalStateAndUnexpectedOwner",
    "FullyQualifiedName~AgentInboxSnapshotRemainsPinnedWhenRemoteHeadAdvances",
    "FullyQualifiedName~AgentInboxBatchRejectsNonRegularCanonicalGitObjects",
    "FullyQualifiedName~AdministrativeCorrectionRejectsStaleIdentityVersionsAndGenuineWorkerYieldWithoutMutation",
    "FullyQualifiedName~ChallengeWorkflowTests",
    "FullyQualifiedName~LedgerMutationStagePerfTests",
    "FullyQualifiedName~WorktreeTests",
    "FullyQualifiedName~JobManagerTests",
]


def run(command: list[str], cwd: Path, *, env: dict[str, str] | None = None,
        stdout_path: Path | None = None) -> subprocess.CompletedProcess[str]:
    if stdout_path is None:
        return subprocess.run(command, cwd=cwd, env=env, text=True,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    stdout_path.parent.mkdir(parents=True, exist_ok=True)
    with stdout_path.open("w", encoding="utf-8") as output:
        return subprocess.run(command, cwd=cwd, env=env, text=True,
                              stdout=output, stderr=subprocess.STDOUT)


def verify_trx(path: Path, label: str, filters: list[str]) -> None:
    if not path.is_file():
        raise RuntimeError(f"{label}: dotnet test did not produce its TRX result file: {path}")
    root = ET.parse(path).getroot()
    counters = next(
        (node for node in root.iter() if node.tag.rsplit("}", 1)[-1] == "Counters"),
        None,
    )
    if counters is None:
        raise RuntimeError(f"{label}: TRX result has no test counters: {path}")
    total = int(counters.attrib.get("total", "0"))
    executed = int(counters.attrib.get("executed", "0"))
    failed = int(counters.attrib.get("failed", "0"))
    if total == 0 or executed == 0:
        raise RuntimeError(f"{label}: filter matched no executed tests ({total=}, {executed=})")
    if failed:
        raise RuntimeError(f"{label}: {failed} test(s) failed in {path}")
    definitions = {}
    for node in root.iter():
        if node.tag.rsplit("}", 1)[-1] == "UnitTest":
            method = next((child for child in node if child.tag.rsplit("}", 1)[-1] == "TestMethod"), None)
            if method is not None:
                definitions[node.attrib["id"]] = method.attrib["className"] + "." + method.attrib["name"]
    passed_names = [definitions[node.attrib["testId"]] for node in root.iter()
                    if node.tag.rsplit("}", 1)[-1] == "UnitTestResult"
                    and node.attrib.get("outcome") == "Passed"]
    for test_filter in filters:
        prefix = "FullyQualifiedName~"
        if not test_filter.startswith(prefix) or not any(test_filter[len(prefix):] in name for name in passed_names):
            raise RuntimeError(f"{label}: no passed executed test for {test_filter!r}")


def git(repo: Path, *args: str) -> str:
    result = run(["git", *args], repo)
    if result.returncode:
        raise RuntimeError(f"git {' '.join(args)} failed:\n{result.stdout}")
    return result.stdout.strip()


def install_harness(harness_bytes: bytes, target_root: Path) -> None:
    target = target_root / HARNESS_FILE
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(harness_bytes)

    project = target_root / PROJECT_FILE
    tree = ET.parse(project, parser=ET.XMLParser(target=ET.TreeBuilder(insert_comments=True)))
    root = tree.getroot()
    existing = [item for item in root.iter("Compile") if item.get("Include") == COMPILE_ITEM]
    markers = [(group, index) for group in root.findall("ItemGroup") for index, item in enumerate(group)
               if item.tag == "Compile" and item.get("Include") == COMPILE_MARKER]
    if len(markers) != 1 or len(existing) > 1:
        raise RuntimeError(f"Ambiguous F# compile order in {project}")
    if not existing:
        group, index = markers[0]
        item = ET.Element("Compile", {"Include": COMPILE_ITEM})
        item.tail = group[index].tail
        group.insert(index, item)
        tree.write(project, encoding="utf-8")


def parse_reports(directory: Path, expected_sha: str) -> dict[str, dict[str, Any]]:
    reports: dict[str, dict[str, Any]] = {}
    for path in sorted(directory.glob("*.json")):
        report = json.loads(path.read_text(encoding="utf-8"))
        actual_sha = report.get("sourceSha")
        if actual_sha != expected_sha:
            raise RuntimeError(
                f"{path.name} was produced from {actual_sha}, expected {expected_sha}"
            )
        reports[path.stem] = report
    required = {
        "agent-inbox",
        "uncertain-publication",
        "worktree-discovery",
        "controlled-transport-delay",
        "controlled-transport-cancellation",
    }
    missing = required - reports.keys()
    if missing:
        raise RuntimeError(f"Missing harness reports: {', '.join(sorted(missing))}")
    return reports


def run_source(source_root: Path, temp_root: Path, label: str, sha: str,
               include_focused: bool, harness_bytes: bytes) -> tuple[dict[str, dict[str, Any]], Path]:
    worktree = temp_root / label
    add = run(["git", "worktree", "add", "--detach", str(worktree), sha], source_root)
    if add.returncode:
        raise RuntimeError(f"Could not create {label} worktree at {sha}:\n{add.stdout}")

    try:
        install_harness(harness_bytes, worktree)
        report_dir = temp_root / "reports" / label
        log_dir = temp_root / "logs" / label
        report_dir.mkdir(parents=True, exist_ok=True)
        env = os.environ.copy()
        env["JARVIS_GIT_PERF_REPORT_DIR"] = str(report_dir)
        env["JARVIS_GIT_PERF_SOURCE_SHA"] = sha

        filters = ["FullyQualifiedName~GitCallBaselineTests"]
        if include_focused:
            filters.extend(FOCUSED_FILTERS)

        for index, test_filter in enumerate(["|".join(filters)]):
            command = [
                "dotnet", "test", str(PROJECT_FILE), "-c", "Release", "-m:1",
                "--filter", test_filter,
                "--logger", f"trx;LogFileName={label}-{index}.trx",
                "--results-directory", str(temp_root / "trx" / label / str(index)),
                "--", "RunConfiguration.MaxCpuCount=1",
            ]
            log_path = log_dir / f"{index}.log"
            result = run(command, worktree, env=env, stdout_path=log_path)
            if result.returncode:
                details = log_path.read_text(encoding="utf-8", errors="replace")
                raise RuntimeError(
                    f"{label} test filter {test_filter!r} failed (exit {result.returncode}); "
                    f"see {log_path}\n{details[-12000:]}"
                )

            trx_path = temp_root / "trx" / label / str(index) / f"{label}-{index}.trx"
            verify_trx(trx_path, f"{label} filter {test_filter!r}", filters)
        return parse_reports(report_dir, sha), worktree
    except Exception:
        run(["git", "worktree", "remove", "--force", str(worktree)], source_root)
        raise


def metric(report: dict[str, Any], path: str, default: Any = 0) -> Any:
    current: Any = report
    for part in path.split("."):
        if not isinstance(current, dict) or part not in current:
            return default
        current = current[part]
    return current


def compare_field(before: dict[str, Any], after: dict[str, Any],
                  report: str, field: str) -> list[str]:
    if field not in before.get(report, {}) or field not in after.get(report, {}):
        return [f"FAIL {report}.{field}: missing required evidence"]
    left = before[report][field]
    right = after[report][field]
    if left == right:
        return []
    return [f"FAIL {report}.{field}: {left!r} -> {right!r}"]


def parser_signature(report: dict[str, Any]) -> list[tuple[int, str]]:
    return [
        (item["population"], item["payloadSha256"])
        for item in report.get("parserPopulations", [])
    ]


def cleanliness_map(report: dict[str, Any]) -> dict[str, str]:
    return {
        item["path"]: item["state"]
        for item in report.get("cleanliness", [])
    }


def compare_reports(before: dict[str, dict[str, Any]],
                    after: dict[str, dict[str, Any]],
                    allow_cleanliness_unknown: bool) -> tuple[list[str], list[str]]:
    failures: list[str] = []
    notes: list[str] = []

    semantic_fields = [
        ("agent-inbox", "inboxResultClass"),
        ("agent-inbox", "inboxSemanticSha256"),
        ("agent-inbox", "snapshotMatchesCanonicalRef"),
        ("agent-inbox", "taskOverviewResultClass"),
        ("agent-inbox", "taskOverviewSemanticSha256"),
        ("agent-inbox", "invalidAgentResultClass"),
        ("agent-inbox", "canonicalRef"),
        ("agent-inbox", "invalidAgentGitProcessStarts"),
        ("uncertain-publication", "initialResultClass"),
        ("uncertain-publication", "recoveryResultClass"),
        ("uncertain-publication", "recoveryReceiptFound"),
        ("uncertain-publication", "recoveryReceiptSemanticSha256"),
        ("uncertain-publication", "receiptAndSnapshotVerified"),
        ("uncertain-publication", "replayRefUnchanged"),
        ("uncertain-publication", "conflictRefUnchanged"),
        ("uncertain-publication", "recoveredTaskSha256"),
        ("uncertain-publication", "replayResultClass"),
        ("uncertain-publication", "changedRequestResultClass"),
        ("uncertain-publication", "canonicalRef"),
        ("worktree-discovery", "actualIdentitySha256"),
        ("worktree-discovery", "actualHeadVerified"),
        ("worktree-discovery", "actualWorktreeCount"),
        ("controlled-transport-delay", "uploadPackWrapperInvocations"),
        ("controlled-transport-cancellation", "pinnedSnapshotAndBlobVerified"),
        ("controlled-transport-cancellation", "canonicalRefUnchanged"),
        ("controlled-transport-cancellation", "pinnedReadResultClass"),
        ("controlled-transport-cancellation", "pinnedReadRemoteFetches"),
        ("controlled-transport-cancellation", "delayedFetchRemoteFetches"),
        ("controlled-transport-cancellation", "cancelledFetchResultClass"),
        ("controlled-transport-cancellation", "cancelledFetchRemoteUploadPackStarts"),
        ("controlled-transport-cancellation", "cancelledFetchStageRefCreated"),
        ("controlled-transport-cancellation", "resumedFetchRemoteFetches"),
        ("controlled-transport-cancellation", "uploadPackWrapperInvocations"),
    ]
    for report_name, field in semantic_fields:
        failures.extend(compare_field(before, after, report_name, field))

    if parser_signature(before["worktree-discovery"]) != parser_signature(after["worktree-discovery"]):
        failures.append("FAIL worktree parser semantic digest/population changed at 0/220/2200")

    before_worktrees = before["worktree-discovery"]
    after_worktrees = after["worktree-discovery"]
    if before_worktrees.get("actualFullPayloadSha256") != after_worktrees.get("actualFullPayloadSha256"):
        left = cleanliness_map(before_worktrees)
        right = cleanliness_map(after_worktrees)
        differences = {
            path: (left.get(path), right.get(path))
            for path in left.keys() | right.keys()
            if left.get(path) != right.get(path)
        }
        only_to_unknown = bool(differences) and all(
            pair[0] in {"clean", "dirty"} and pair[1] == "unknown" for pair in differences.values()
        )
        if allow_cleanliness_unknown and only_to_unknown:
            notes.append(f"INFO Worktree IsClean changed only to unknown: {differences}")
        else:
            failures.append(f"FAIL worktree full payload changed: {differences}")

    for label, reports in (("baseline", before), ("candidate", after)):
        for scenario, fields in {
            "agent-inbox": ("snapshotMatchesCanonicalRef",),
            "uncertain-publication": ("receiptAndSnapshotVerified", "replayRefUnchanged", "conflictRefUnchanged"),
            "worktree-discovery": ("actualHeadVerified",),
            "controlled-transport-cancellation": ("pinnedSnapshotAndBlobVerified", "canonicalRefUnchanged"),
        }.items():
            for field in fields:
                if reports[scenario].get(field) is not True:
                    failures.append(f"FAIL {label}: {scenario}.{field} was not verified")
        if reports["agent-inbox"].get("invalidAgentGitProcessStarts") != 0:
            failures.append(f"FAIL {label}: malformed inbox ID performed Git work")
        delay = reports["controlled-transport-delay"]
        if delay.get("uploadPackWrapperInvocations", 0) < 1:
            failures.append(f"FAIL {label}: controlled upload-pack delay was not exercised")
        cancellation = reports["controlled-transport-cancellation"]
        if cancellation.get("cancelledFetchResultClass") != "Cancelled":
            failures.append(f"FAIL {label}: real remote fetch cancellation was not observed")
        if cancellation.get("cancelledFetchStageRefCreated") is not False:
            failures.append(f"FAIL {label}: cancelled fetch published a local stage ref")
        if cancellation.get("pinnedReadRemoteFetches") != 0:
            failures.append(f"FAIL {label}: pinned read triggered a remote fetch")
        if cancellation.get("delayedFetchRemoteFetches") != 1:
            failures.append(f"FAIL {label}: delayed fetch was retried")
        if cancellation.get("resumedFetchRemoteFetches") != 1:
            failures.append(f"FAIL {label}: recovery fetch did not perform exactly one fetch")
        if cancellation.get("uploadPackWrapperInvocations") != 3:
            failures.append(f"FAIL {label}: delayed/cancelled/recovery transport count was not exactly three")

    return failures, notes


def print_metrics(label: str, reports: dict[str, dict[str, Any]]) -> None:
    inbox = reports["agent-inbox"]
    write = reports["uncertain-publication"]
    worktree = reports["worktree-discovery"]
    delay = reports["controlled-transport-delay"]
    cancellation = reports["controlled-transport-cancellation"]
    print(f"\n{label}:")
    print(
        "  AgentInbox cold/warm: "
        f"{metric(inbox, 'inboxCold.elapsedMilliseconds'):.2f}/"
        f"{metric(inbox, 'inboxWarm.elapsedMilliseconds'):.2f} ms; "
        f"Git starts {metric(inbox, 'inboxColdTrace.processStarts')}, "
        f"fetches {metric(inbox, 'inboxColdTrace.fetchStarts')}"
    )
    print(
        "  TaskOverview cold/warm: "
        f"{metric(inbox, 'taskOverviewCold.elapsedMilliseconds'):.2f}/"
        f"{metric(inbox, 'taskOverviewWarm.elapsedMilliseconds'):.2f} ms; "
        f"Git starts {metric(inbox, 'taskOverviewColdTrace.processStarts')}, "
        f"fetches {metric(inbox, 'taskOverviewColdTrace.fetchStarts')}"
    )
    print(
        "  uncertain write: "
        f"{metric(write, 'initialTrace.elapsedMilliseconds'):.2f} ms; "
        f"Git starts {metric(write, 'initialTrace.processStarts')}, "
        f"fetches {metric(write, 'initialTrace.fetchStarts')}, "
        f"pushes {metric(write, 'initialTrace.pushStarts')}"
    )
    print(
        "  Worktrees: "
        f"{metric(worktree, 'actualTrace.elapsedMilliseconds'):.2f} ms; "
        f"Git starts {metric(worktree, 'actualTrace.processStarts')}, "
        f"status commands {metric(worktree, 'actualTrace.statusStarts')}"
    )
    print(
        "  controlled transport: "
        f"{metric(delay, 'trace.elapsedMilliseconds'):.2f} ms; "
        f"upload-pack invocations {delay.get('uploadPackWrapperInvocations', 0)}"
    )


    print(
        "  pinned/delayed/cancelled/recovery: "
        f"pinned fetches {cancellation.get('pinnedReadRemoteFetches')}, "
        f"delayed fetches {cancellation.get('delayedFetchRemoteFetches')}, "
        f"cancel={cancellation.get('cancelledFetchResultClass')}, "
        f"recovery fetches {cancellation.get('resumedFetchRemoteFetches')}, "
        f"upload-pack invocations {cancellation.get('uploadPackWrapperInvocations')}"
    )

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate-sha", required=True,
                        help="Exact candidate source commit to compare with the frozen baseline.")
    parser.add_argument("--baseline-sha", default=BASELINE_SHA)
    parser.add_argument("--repo-root", type=Path, default=None)
    parser.add_argument("--output-dir", type=Path, default=Path("/tmp/jarvis-git-call-comparison"))
    parser.add_argument("--include-focused-suite", action="store_true",
                        help="Also run existing focused task/inbox/write/worktree/recovery suites.")
    parser.add_argument("--allow-cleanliness-unknown", action="store_true",
                        help="Allow the explicit Worktree IsClean Some(bool)->None contract change.")
    args = parser.parse_args()

    repo = args.repo_root.resolve() if args.repo_root else Path(__file__).resolve().parents[3]
    repo = repo.resolve()
    for sha in (args.baseline_sha, args.candidate_sha):
        git(repo, "rev-parse", "--verify", f"{sha}^{{commit}}")

    args.output_dir.mkdir(parents=True, exist_ok=True)
    harness_bytes = (repo / HARNESS_FILE).read_bytes()
    harness_sha = hashlib.sha256(harness_bytes).hexdigest()
    temp_root = Path(tempfile.mkdtemp(prefix="jarvis-git-call-compare-"))
    (temp_root / "harness.fs").write_bytes(harness_bytes)
    worktrees: list[Path] = []
    successful = False
    try:
        baseline, baseline_worktree = run_source(
            repo, temp_root, "baseline", args.baseline_sha, args.include_focused_suite, harness_bytes
        )
        worktrees.append(baseline_worktree)
        candidate, candidate_worktree = run_source(
            repo, temp_root, "candidate", args.candidate_sha, args.include_focused_suite, harness_bytes
        )
        worktrees.append(candidate_worktree)

        print_metrics("Baseline " + args.baseline_sha[:12], baseline)
        print_metrics("Candidate " + args.candidate_sha[:12], candidate)
        failures, notes = compare_reports(
            baseline, candidate, args.allow_cleanliness_unknown
        )
        for note in notes:
            print(note)
        for failure in failures:
            print(failure)

        combined = {
            "harnessSha256": harness_sha,
            "baselineSha": args.baseline_sha,
            "candidateSha": args.candidate_sha,
            "semanticEquivalent": not failures,
            "failures": failures,
            "notes": notes,
            "baseline": baseline,
            "candidate": candidate,
        }
        (args.output_dir / "comparison.json").write_text(
            json.dumps(combined, indent=2, sort_keys=True), encoding="utf-8"
        )
        shutil.copy2(temp_root / "harness.fs", args.output_dir / "harness.fs")
        for name in ("reports", "logs", "trx"):
            source = temp_root / name
            if source.exists():
                shutil.copytree(source, args.output_dir / name, dirs_exist_ok=True)
        successful = True
        print(f"\nComparison report: {args.output_dir / 'comparison.json'}")
        return 1 if failures else 0
    finally:
        for worktree in reversed(worktrees):
            run(["git", "worktree", "remove", "--force", str(worktree)], repo)
        if successful:
            shutil.rmtree(temp_root, ignore_errors=True)
        else:
            print(f"Temporary logs/reports retained at {temp_root}", file=sys.stderr)


if __name__ == "__main__":
    sys.exit(main())
