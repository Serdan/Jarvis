#!/usr/bin/env python3
"""Read-only evidence inventory after reconnect. Never resumes or launches."""
import argparse
import json
from pathlib import Path
import sys

from qualify import sha


def read_json(path):
    with path.open(encoding="utf-8") as f:
        return json.load(f)


def inspect_stage(stage):
    if not stage.exists():
        return {"state": "not_started"}
    try:
        terminal_path = stage / "terminal.json"
        if not terminal_path.is_file():
            return {"state": "unknown_no_terminal_receipt"}
        terminal = read_json(terminal_path)
        files = terminal.get("sha256")
        if not isinstance(files, dict) or not files:
            return {"state": "terminal_without_hashes", "terminal": terminal}
        mismatches = []
        for rel, expected in files.items():
            path = stage / rel
            if (not isinstance(rel, str) or Path(rel).is_absolute()
                    or ".." in Path(rel).parts or path.is_symlink()
                    or not path.resolve().is_relative_to(stage.resolve())
                    or not path.is_file() or sha(path) != expected):
                mismatches.append(rel)
        return {"state": "hash_mismatch" if mismatches else "receipt_readback",
                "mismatches": mismatches, "terminal": terminal,
                "terminal_sha256": sha(terminal_path)}
    except (ValueError, OSError, TypeError, AttributeError) as exc:
        return {"state": "invalid_evidence", "error": str(exc)}


def inspect_run(output):
    evidence = output / "evidence"
    started = read_json(evidence / "run-started.json")
    matrix = started["matrix"]
    observations = {}
    for stage in matrix:
        name = stage["name"]
        if not isinstance(name, str) or not name or Path(name).name != name or name in (".", ".."):
            raise ValueError("invalid stage name")
        if name in observations:
            raise ValueError("duplicate stage name")
        observations[name] = inspect_stage(evidence / name)
    source_path = evidence / "source.json"
    terminal_path = evidence / "run-terminal.json"
    return {"run_started": started,
            "run_started_sha256": sha(evidence / "run-started.json"),
            "source": read_json(source_path) if source_path.is_file() else None,
            "source_sha256": sha(source_path) if source_path.is_file() else None,
            "run_terminal": read_json(terminal_path) if terminal_path.is_file() else None,
            "run_terminal_sha256": sha(terminal_path) if terminal_path.is_file() else None,
            "stage_observations": observations,
            "acceptance": "independent_review_required"}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("output", type=Path)
    a = p.parse_args()
    try:
        report = inspect_run(a.output.resolve())
    except (OSError, ValueError, KeyError, TypeError) as exc:
        print(json.dumps({"state": "invalid_or_missing_run", "error": str(exc)}))
        return 2
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0 if (report["source"] is not None and report["run_terminal"] is not None
                 and all(s["state"] == "receipt_readback"
                         for s in report["stage_observations"].values())) else 2


if __name__ == "__main__":
    sys.exit(main())
