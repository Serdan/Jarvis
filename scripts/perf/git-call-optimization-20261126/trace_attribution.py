#!/usr/bin/env python3
"""Attribute retained per-call Trace2 streams without adding child events to process counts."""
import argparse
from collections import Counter
import json
from pathlib import Path


def attribute(report):
    root = Path(report["FixtureRoot"]).resolve(strict=True)
    result = {}
    for m in report["Measurements"]:
        path = Path(m["TracePath"]).resolve()
        if path.parent != root / "traces":
            raise ValueError(f"trace is outside fixture: {path}")
        if not path.exists():
            if m["GitProcessStarts"] or m["RemoteFetches"]:
                raise ValueError(f"missing nonempty trace: {path}")
            events = []
        else:
            events = [json.loads(line) for line in path.read_text().splitlines()]
        counts = Counter()
        children = Counter()
        for event in events:
            kind = event["event"]
            if kind == "start":
                argv = event.get("argv", [])
                if not argv:
                    raise ValueError("start without argv")
                executable = Path(argv[0]).name
                if executable == "git" or executable.startswith("git-"):
                    counts["git_starts"] += 1
                    counts["nested_git_starts" if "/" in event["sid"] else "root_git_starts"] += 1
                    if executable == "git" and "fetch" in argv[1:]:
                        counts["fetch_starts"] += 1
                else:
                    counts["other_traced_starts"] += 1
            elif kind == "child_start":
                cls = event.get("child_class", "?")
                children[cls] += 1
                counts["child_launch_events"] += 1
                if cls.startswith("transport/"):
                    counts["transport_launch_events"] += 1
                    # A transport launch may itself execute a Git program, often
                    # via a shell. It is not another confirmed Git start.
                    if event.get("use_shell"):
                        counts["transport_shell_launch_events"] += 1
        if counts["git_starts"] != m["GitProcessStarts"] or counts["fetch_starts"] != m["RemoteFetches"]:
            raise ValueError(f"{m['Scenario']}: reported counts differ from raw Trace2: {counts}")
        result[m["Scenario"]] = {**dict(counts), "child_classes": dict(children)}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    result = attribute(json.loads(args.report.read_text()))
    total = Counter()
    for counts in result.values():
        total.update({k: v for k, v in counts.items() if isinstance(v, int)})
    output = json.dumps({"totals": dict(total), "scenarios": result}, indent=2)
    if args.out:
        args.out.write_text(output + "\n")
    print(json.dumps(dict(total), sort_keys=True))


if __name__ == "__main__":
    main()
