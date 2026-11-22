#!/usr/bin/env python3
"""Read-only /proc sampler rooted at one bounded disposable dotnet test invocation.
Records only pids, parent pids, process comm, threads and memory; never arguments.
Distinct Git pid count is not a substitute for LedgerStore's per-operation counter.
"""
import argparse
import json
import os
import time
from pathlib import Path

def processes():
    found = {}
    for entry in Path("/proc").iterdir():
        if not entry.name.isdecimal():
            continue
        try:
            raw = (entry / "stat").read_text()
            close = raw.rfind(")")
            name = raw[raw.index("(")+1:close]
            fields = raw[close + 2:].split()
            found[int(entry.name)] = {
                "ppid": int(fields[1]),
                "comm": name,
                "threads": int(fields[17]),
                "rss_kib": int(fields[21]) * os.sysconf("SC_PAGE_SIZE") // 1024,
            }
        except (OSError, IndexError, ValueError):
            pass
    return found

def descendants(procs, root):
    tree = {root}
    for _ in range(len(procs) + 1):
        next_tree = tree | {pid for pid, item in procs.items() if item["ppid"] in tree}
        if next_tree == tree:
            break
        tree = next_tree
    return {pid: procs[pid] for pid in tree if pid in procs}

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=int, required=True)
    parser.add_argument("--stop-file", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--summary", required=True)
    args = parser.parse_args()
    seen_git = set()
    peaks = {"peak_git_concurrent": 0, "peak_descendants": 0, "peak_threads": 0, "peak_rss_kib": 0}
    started = time.monotonic()
    rows = 0
    with open(args.output, "w", encoding="utf-8") as output:
        while True:
            procs = processes()
            tree = descendants(procs, args.root)
            git = [pid for pid, p in tree.items() if p["comm"] == "git"]
            seen_git.update(git)
            row = {
                "elapsed_s": round(time.monotonic() - started, 3),
                "git_pids": git,
                "descendant_count": len(tree),
                "threads": sum(p["threads"] for p in tree.values()),
                "rss_kib": sum(p["rss_kib"] for p in tree.values()),
                "processes": [{"pid": pid, "ppid": p["ppid"], "comm": p["comm"]}
                              for pid, p in tree.items() if p["comm"] == "git"],
            }
            rows += 1
            peaks["peak_git_concurrent"] = max(peaks["peak_git_concurrent"], len(git))
            peaks["peak_descendants"] = max(peaks["peak_descendants"], row["descendant_count"])
            peaks["peak_threads"] = max(peaks["peak_threads"], row["threads"])
            peaks["peak_rss_kib"] = max(peaks["peak_rss_kib"], row["rss_kib"])
            output.write(json.dumps(row, separators=(",", ":")) + "\n")
            if rows % 20 == 0:
                output.flush()
            if os.path.exists(args.stop_file) or args.root not in procs:
                break
            time.sleep(0.15)
    with open(args.summary, "w", encoding="utf-8") as output:
        json.dump({
            "root_pid": args.root, "sample_count": rows, "sample_period_s": 0.15,
            "git_unique_seen": len(seen_git), "observed_git_pids": sorted(seen_git),
            "duration_s": round(time.monotonic() - started, 3),
            **peaks,
        }, output, indent=2)
        output.write("\n")

if __name__ == "__main__":
    main()
