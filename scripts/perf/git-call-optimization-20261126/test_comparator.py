#!/usr/bin/env python3
"""Exercise the comparator with real retained fixture reports and altered evidence.

Usage: python3 -B test_comparator.py BASELINE.json CANDIDATE.json
The fixture repositories and original reports are never modified.
"""
import copy
import json
import subprocess
import sys
import tempfile
from pathlib import Path

from fixture_identity import digest

COMPARE = Path(__file__).with_name("compare.py")


def sample(report, name):
    return next(m for m in report["Measurements"] if m["Scenario"] == name)


def alter_payload(report, name, change):
    m = sample(report, name)
    outer = json.loads(m["ToolResultProjection"])
    inner = json.loads(outer["content"][0]["text"])
    replacement = change(inner)
    outer["content"][0]["text"] = json.dumps(inner if replacement is None else replacement)
    m["ToolResultProjection"] = json.dumps(outer)


def main():
    baseline, candidate = map(Path, sys.argv[1:])
    original = json.loads(candidate.read_text())
    cold = "read-file-worktree-cold"
    retry = "retry-same-operation"

    def check(path, expected, label):
        p = subprocess.run([sys.executable, "-B", str(COMPARE), str(baseline), str(path)],
                           text=True, capture_output=True)
        if p.returncode != expected or (expected and "FAILED:" not in p.stdout):
            raise AssertionError(f"{label}: exit {p.returncode}\n{p.stdout}\n{p.stderr}")
        print(label, "passed")

    check(candidate, 0, "unmodified pair")
    cases = {
        "missing scenario": lambda r: r["Measurements"].pop(),
        "duplicate scenario": lambda r: r["Measurements"].append(r["Measurements"][0]),
        "missing exact refs": lambda r: sample(r, retry).update(RefTargetsAfter=[]),
        "retry ref changed": lambda r: sample(r, retry).update(RefTargetsAfter=sample(r, "register-agent")["RefTargetsBefore"]),
        "wrong snapshot": lambda r: alter_payload(r, "get-task", lambda x: x.update(snapshotCommit="a" * 40)),
        "wrong receipt": lambda r: alter_payload(r, retry, lambda x: x["operation"].update(OriginalResultHash="b" * 64)),
        "wrong fresh content": lambda r: alter_payload(r, "read-file-after-head-update", lambda x: "wrong"),
        "wrong cold content": lambda r: alter_payload(r, cold, lambda x: "wrong"),
        "invalid response digest": lambda r: sample(r, cold).update(ResponseDigest="c" * 64),
        "unauthorized Git work": lambda r: sample(r, "unauthorized-register-agent").update(GitProcessStarts=1),
        "inbox activation": lambda r: alter_payload(r, "agent-inbox", lambda x: x["InstructionLayers"].update(SnapshotCommit="d" * 40)),
        "wrong raw tool result": lambda r: sample(r, cold).update(ToolResultProjection=None),
        "dirty source": lambda r: r.update(MeasuredSourceClean=False),
    }
    with tempfile.TemporaryDirectory(prefix="jarvis-comparator-rejections-") as tmp:
        path = Path(tmp) / "modified.json"
        for label, mutate in cases.items():
            r = copy.deepcopy(original)
            mutate(r)
            path.write_text(json.dumps(r))
            check(path, 1, label)
    print(f"{len(cases)} negative controls rejected")


if __name__ == "__main__":
    main()
