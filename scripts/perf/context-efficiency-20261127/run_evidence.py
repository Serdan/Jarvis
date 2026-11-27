#!/usr/bin/env python3
"""Build and run the pinned harness from fresh local snapshots; retain receipts."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import subprocess
import time

DIRECTORY = "scripts/perf/context-efficiency-20261127"
PROJECT = "ContextEfficiency.Harness.fsproj"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def clean_sha(root):
    if Path(git(root, "rev-parse", "--show-toplevel")).resolve() != root:
        raise ValueError(f"Not a checkout root: {root}")
    if git(root, "status", "--porcelain=v1", "--untracked-files=all"):
        raise ValueError(f"Dirty checkout: {root}")
    return git(root, "rev-parse", "HEAD")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", required=True, type=Path)
    parser.add_argument("--source-sha", help="Exact locally available runtime commit; default is clean checkout HEAD")
    parser.add_argument("--evidence-dir", required=True, type=Path,
                        help="Must not exist; keep entire directory as evidence")
    parser.add_argument("--history", required=True, type=int)
    parser.add_argument("--transport-delay-ms", type=int, default=20)
    parser.add_argument("--representative", action="store_true")
    parser.add_argument("--ceiling-only", action="store_true")
    parser.add_argument("--job-threshold-ms", type=int)
    args = parser.parse_args()
    source = args.source_root.resolve()
    harness = Path(__file__).resolve().parents[3]
    evidence = args.evidence_dir.resolve()
    for root in (source, harness):
        if evidence.is_relative_to(root):
            raise ValueError("Evidence directory must be outside source/harness checkouts")
    source_checkout_sha, harness_sha = clean_sha(source), clean_sha(harness)
    source_sha = args.source_sha or source_checkout_sha
    if not re.fullmatch(r"(?:[a-f0-9]{40}|[a-f0-9]{64})", source_sha):
        raise ValueError("Source SHA must be a full lowercase commit OID")
    if git(source, "rev-parse", source_sha + "^{commit}") != source_sha:
        raise ValueError("Source SHA is not an exact commit")
    evidence.mkdir(parents=True, exist_ok=False)
    receipt_path = evidence / "report.json.receipt.json"
    runtime, harness_copy = evidence / "runtime", evidence / "harness"
    output = evidence / "build"
    report = evidence / "report.json"
    env = os.environ.copy()
    # Do not silently inherit a different promotion threshold.
    env.pop("JARVIS_TOOL_JOB_THRESHOLD_MS", None)
    if args.job_threshold_ms is not None:
        env["JARVIS_TOOL_JOB_THRESHOLD_MS"] = str(args.job_threshold_ms)
    receipt = {
        "schema": 1, "status": "unqualified", "source_sha": source_sha,
        "harness_sha": harness_sha, "source_checkout": str(source),
        "source_checkout_sha": source_checkout_sha,
        "harness_checkout": str(harness), "runtime_root": str(runtime),
        "harness_root": str(harness_copy), "job_threshold_ms": args.job_threshold_ms,
        "commands": [],
    }

    def save():
        receipt_path.write_text(json.dumps(receipt, indent=2) + "\n")

    def command(label, argv, cwd):
        argv = [str(x) for x in argv]
        log = evidence / (label + ".log")
        entry = {"label": label, "argv": argv, "cwd": str(cwd), "log": str(log)}
        receipt["commands"].append(entry)
        save()
        start = time.monotonic()
        with log.open("xb") as stream:
            result = subprocess.run(argv, cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT)
        entry.update(exit_code=result.returncode, log_sha256=digest(log),
                     elapsed_seconds=time.monotonic() - start)
        save()
        if result.returncode:
            raise RuntimeError(f"{label} exited {result.returncode}; see {log}")

    try:
        # A local clone with no checkout and then an exact detached checkout ensures
        # ignored bin/obj and developer-only files cannot leak into this build.
        for label, root, sha, dest in (
            ("runtime", source, source_sha, runtime),
            ("harness", harness, harness_sha, harness_copy),
        ):
            command(label + "-clone", ["git", "clone", "--shared", "--no-checkout", "--", root, dest], evidence)
            command(label + "-checkout", ["git", "checkout", "--detach", sha], dest)
            if clean_sha(dest) != sha:
                raise ValueError(f"Snapshot mismatch: {dest}")
        project = harness_copy / DIRECTORY / PROJECT
        client, server = runtime / "Client/Client.fsproj", runtime / "Server/Server.fsproj"
        references = [client.resolve(strict=True), server.resolve(strict=True)]
        if any(not ref.is_relative_to(runtime) for ref in references):
            raise ValueError("Project reference escaped runtime snapshot")
        receipt["project"] = str(project)
        receipt["references"] = [str(ref) for ref in references]
        receipt["harness_inputs"] = {name: digest(project.parent / name)
                                     for name in (PROJECT, "Program.fs", "summarize.py", "run_evidence.py")}
        command("sdk", ["dotnet", "--version"], runtime)
        command("build", ["dotnet", "build", project, "-c", "Release", "--output", output,
                          "-p:JarvisClientProject=" + str(references[0]),
                          "-p:JarvisServerProject=" + str(references[1])], runtime)
        dll = output / "ContextEfficiency.Harness.dll"
        receipt["build_files"] = {str(f.relative_to(output)): digest(f)
                                  for f in sorted(output.rglob("*")) if f.is_file()}
        if not dll.is_file():
            raise ValueError("Fresh harness assembly missing")
        invocation = ["dotnet", dll, "--source-root", runtime, "--history", str(args.history),
                      "--transport-delay-ms", str(args.transport_delay_ms), "--out", report]
        if args.representative:
            invocation.append("--representative")
        if args.ceiling_only:
            invocation.append("--ceiling-only")
        command("run", invocation, runtime)
        measured = json.loads(report.read_text())
        if measured["SourceSha"] != source_sha or Path(measured["SourceRoot"]).resolve() != runtime:
            raise ValueError("Report/source identity mismatch")
        if clean_sha(runtime) != source_sha or clean_sha(harness_copy) != harness_sha:
            raise ValueError("Snapshot changed during build/run")
        if clean_sha(source) != source_checkout_sha or clean_sha(harness) != harness_sha:
            raise ValueError("Original checkout changed during build/run")
        if any(digest(output / name) != sha for name, sha in receipt["build_files"].items()):
            raise ValueError("Build output changed during execution")
        receipt["artifact_sha256"] = digest(report)
        receipt["status"] = "bound"
        save()
        print(report)
    except BaseException as error:
        receipt["status"] = "unqualified"
        receipt["error"] = str(error)
        save()
        raise


if __name__ == "__main__":
    main()
