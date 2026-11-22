#!/usr/bin/env python3
"""Verify the exact-parent .NET11 Release baseline's persisted process counts.

Read-only. The baseline test must have reached the report stage; a failed
frozen composition does not emit these reports because it fails at cold.
"""
import argparse
import json
from pathlib import Path

PARENT = "dcac884fc061869572b12a03befc12f11c009f2f"


def inspect(directory):
    rows = []
    for count in (0, 220, 2200):
        path = directory / f"agent-inbox-{count}-run-1.json"
        record = json.loads(path.read_text(encoding="utf-8"))
        if record.get("sourceSha") != PARENT:
            raise ValueError(f"Wrong source for {path}: {record.get('sourceSha')}")
        observed = {
            "fixtureRecords": count,
            "source": record["sourceSha"],
            "cold": record["cold"]["maxGitProcesses"],
            "warmFirst": record["warmFirst"]["maxGitProcesses"],
            "warmSampleMax": record["warmSamples"]["maxGitProcesses"],
            "warmSampleCount": record["warmSamples"]["count"],
            "afterHeadAdvance": record["afterHeadAdvance"]["maxGitProcesses"],
        }
        if (observed["cold"], observed["warmFirst"], observed["warmSampleMax"],
            observed["warmSampleCount"], observed["afterHeadAdvance"]) != (11, 12, 12, 30, 13):
            raise ValueError(f"Baseline deviated; do not assert 11+2: {observed}")
        rows.append(observed)
    return rows


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("baseline_report_directory", type=Path)
    args = parser.parse_args()
    print(json.dumps(inspect(args.baseline_report_directory), indent=2))


if __name__ == "__main__":
    main()
