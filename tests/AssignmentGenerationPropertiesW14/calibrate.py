#!/usr/bin/env python3
"""Calibrate generation fencing against a real temporary production mutant.

Run from the assigned isolated worktree, supplying the exact committed test SHA.
The archive is local-only; the registered checkout is never modified.
"""
import argparse
import hashlib
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile

TRACE = "1,0,reissue-new,0;2,0,stale-generation,0"
PROJECT = "tests/AssignmentGenerationPropertiesW14/AssignmentGenerationPropertiesW14.fsproj"
SOURCE = "Client/Modules/ReviewWorkflow.fs"
NEEDLE = "                           || attempt.AssignmentGeneration <> generation\n"


def run_case(directory, label):
    env = os.environ.copy()
    env["JARVIS_W14_TRACE"] = TRACE
    command = [
        "dotnet", "test", PROJECT, "-c", "Release",
        "--filter", "FullyQualifiedName~GeneratedAssignmentHistories",
        "--logger", "console;verbosity=detailed",
    ]
    print(f"{label}: {command!r}, trace={TRACE}", flush=True)
    result = subprocess.run(
        command, cwd=directory, env=env, text=True,
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False,
    )
    print(result.stdout, flush=True)
    print(f"{label}: exit={result.returncode}", flush=True)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("commit", help="Exact committed source/test SHA to archive")
    args = parser.parse_args()
    commit = subprocess.check_output(
        ["git", "rev-parse", "--verify", args.commit + "^{commit}"], text=True
    ).strip()
    if args.commit != commit:
        raise SystemExit("Supply the full exact commit SHA, not a moving ref.")
    print(f"archived commit={commit}", flush=True)
    with tempfile.TemporaryDirectory(prefix="jarvis-w14-generation-mutant-") as root:
        with subprocess.Popen(["git", "archive", commit], stdout=subprocess.PIPE) as archive:
            with tarfile.open(fileobj=archive.stdout, mode="r|") as files:
                files.extractall(root, filter="data")
            if archive.wait() != 0:
                raise RuntimeError("git archive failed")
        directory = Path(root)
        source = directory / SOURCE
        original = source.read_bytes()
        original_digest = hashlib.sha256(original).hexdigest()
        baseline = run_case(directory, "unmodified")
        if baseline.returncode != 0 or f"trace={TRACE}; realized=" not in baseline.stdout:
            raise RuntimeError("Unmodified minimized experiment did not execute and pass.")
        text = original.decode("utf-8")
        start = text.index("    let private resumeAgentCore ")
        stop = text.index("    let resumeAgentWithSession ", start)
        segment = text[start:stop]
        if segment.count(NEEDLE) != 1:
            raise RuntimeError("Expected exactly one ResumeAgent generation guard.")
        mutant = (text[:start] + segment.replace(NEEDLE, "", 1) + text[stop:]).encode("utf-8")
        print(f"original sha256={original_digest}", flush=True)
        print(f"mutant sha256={hashlib.sha256(mutant).hexdigest()}", flush=True)
        print("Mutation: remove only the assignment-generation inequality guard in resumeAgentCore.", flush=True)
        try:
            source.write_bytes(mutant)
            red = run_case(directory, "mutant")
            if (red.returncode == 0
                    or "first divergence step=2,0,stale-generation,0" not in red.stdout
                    or 'Prediction Deny "generation", actual result Ok' not in red.stdout):
                raise RuntimeError("Mutant was not detected at the intended semantic assertion.")
        finally:
            source.write_bytes(original)
            if hashlib.sha256(source.read_bytes()).hexdigest() != original_digest:
                raise RuntimeError("Failed to restore disposable source.")
        print("Calibration passed: unmodified GREEN, intended mutant RED, disposable source restored.", flush=True)


if __name__ == "__main__":
    main()
