#!/usr/bin/env python3
"""One-shot source-exact, unfiltered native .NET 11 Release client diagnostic.
Uses private HOME/TMP/NuGet, bounded wall time, no real profile or browser.
Avoids shell execution, main writes, and old uncertain job replays.
"""
import json
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

EXPECTED = "dcac884fc061869572b12a03befc12f11c009f2f"
BRANCH = "test/automation-client-release-diagnostics-worker22-20261122"
TIMEOUT = 900
WORKSPACE = Path(__file__).resolve().parents[3]

def git(*args):
    return subprocess.run(["git", *args], cwd=WORKSPACE, text=True,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          check=True).stdout.strip()

def preflight():
    head = git("rev-parse", "HEAD")
    if head != EXPECTED or git("branch", "--show-current") != BRANCH:
        raise RuntimeError(f"source/ref mismatch head={head}")
    dirty = git("status", "--porcelain", "--untracked-files=all")
    allowed = ("?? scripts/tests/automation-client-release-diagnostics-20261122/",
               "?? docs/testing/automation-client-release-diagnostics-20261122.md")
    other = [line for line in dirty.splitlines()
             if not any(line.startswith(prefix) for prefix in allowed)]
    if other:
        raise RuntimeError("unrelated dirty source: " + str(other))
    result = subprocess.run(["dotnet", "--version"], cwd=WORKSPACE,
                            capture_output=True, text=True, check=True)
    sdk = result.stdout.strip()
    if sdk != "11.0.100":
        raise RuntimeError(f"SDK must be 11.0.100, found {sdk}")
    return head, sdk

def main():
    try:
        head, sdk = preflight()
    except Exception as exc:
        print(f"FAIL_CLOSED_PREFLIGHT: {exc}", file=sys.stderr, flush=True)
        return 42
    previous_umask = os.umask(0o077)
    try:
        base = Path(tempfile.mkdtemp(prefix="jarvis-client-release-w22-main-", dir="/tmp"))
    finally:
        os.umask(previous_umask)
    for item in ("home", "tmp", "nuget", "results", "xdg-config", "xdg-data", "xdg-cache"):
        (base / item).mkdir(mode=0o700)
    env = dict(os.environ)
    env.update({
        "HOME": str(base / "home"),
        "USERPROFILE": str(base / "home"),
        "TMPDIR": str(base / "tmp"),
        "TEMP": str(base / "tmp"),
        "TMP": str(base / "tmp"),
        "NUGET_PACKAGES": str(base / "nuget"),
        "DOTNET_CLI_HOME": str(base / "home"),
        "XDG_CONFIG_HOME": str(base / "xdg-config"),
        "XDG_DATA_HOME": str(base / "xdg-data"),
        "XDG_CACHE_HOME": str(base / "xdg-cache"),
        "GIT_CONFIG_GLOBAL": "/dev/null",
        "GIT_CONFIG_NOSYSTEM": "1",
        "GIT_TERMINAL_PROMPT": "0",
        "GCM_INTERACTIVE": "Never",
        "DOTNET_NOLOGO": "1",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "NUGET_CREDENTIALPROVIDERS_PATH": str(base / "no-credential-providers")
    })
    for key in ("GIT_ASKPASS", "SSH_ASKPASS", "SSH_AUTH_SOCK", "GIT_CONFIG_SYSTEM"):
        env.pop(key, None)
    argv = ["dotnet", "test", "Client.Tests/Client.Tests.fsproj", "-c", "Release",
            "-m:1", "--logger", "trx;LogFileName=client-full.trx",
            "--results-directory", str(base / "results"), "--",
            "NUnit.NumberOfTestWorkers=0"]
    manifest = {"source": head, "sdk": sdk, "branch": BRANCH, "cwd": str(WORKSPACE),
                "scratch": str(base), "start_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "cmd": argv, "test_filter": None, "nunit_workers": 0, "timeout_s": TIMEOUT}
    (base / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print("EVIDENCE_DIR=" + str(base), flush=True)
    print(f"SOURCE_SHA={head} SDK={sdk} FULL_UNFILTERED nunit_workers=0", flush=True)
    import monitor_proc
    observed = set()
    peak = {"peak_git_concurrent": 0, "peak_descendants": 0, "peak_threads": 0,
            "peak_rss_kib": 0}
    rows = 0
    timed_out = False
    start = time.monotonic()
    with (base / "terminal.log").open("w") as terminal, (base / "proc-samples.jsonl").open("w") as samples:
        child = subprocess.Popen(argv, cwd=WORKSPACE, env=env, stdout=terminal,
                                 stderr=subprocess.STDOUT, start_new_session=True)
        while child.poll() is None:
            now = time.monotonic()
            if now - start >= TIMEOUT:
                timed_out = True
                import signal
                os.killpg(child.pid, signal.SIGTERM)
                try:
                    child.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    os.killpg(child.pid, signal.SIGKILL)
                    child.wait()
                break
            procs = monitor_proc.processes()
            tree = monitor_proc.descendants(procs, child.pid)
            git_pids = [pid for pid, p in tree.items() if p["comm"] == "git"]
            observed.update(git_pids)
            row = {"elapsed_s": round(now - start, 3), "git_pids": git_pids,
                   "descendant_count": len(tree), "threads": sum(x["threads"] for x in tree.values()),
                   "rss_kib": sum(x["rss_kib"] for x in tree.values())}
            rows += 1
            for key, value in (("peak_git_concurrent", len(git_pids)),
                               ("peak_descendants", row["descendant_count"]),
                               ("peak_threads", row["threads"]),
                               ("peak_rss_kib", row["rss_kib"])):
                peak[key] = max(peak[key], value)
            samples.write(json.dumps(row, separators=(",", ":")) + "\n")
            if rows % 20 == 0:
                samples.flush()
            time.sleep(0.15)
        terminal.flush()
        exit_code = child.wait()
    elapsed = round(time.monotonic() - start, 3)
    summary = {"duration_s": elapsed, "observed_git_pids": sorted(observed),
               "git_unique_seen": len(observed), "sample_count": rows,
               "sample_period_s": 0.15, "process_exit": exit_code,
               "timed_out": timed_out, **peak}
    (base / "process-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    trx = [str(p) for p in (base / "results").rglob("*.trx")]
    manifest.update({"end_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                     "exit_code": exit_code, "timed_out": timed_out,
                     "trx_paths": trx, "source_after": git("rev-parse", "HEAD"),
                     "process_summary": summary})
    (base / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"EXIT={exit_code} TIMEOUT={timed_out} TRX_COUNT={len(trx)} ELAPSED={elapsed}s", flush=True)
    print("PROCESS_SUMMARY=" + json.dumps(summary), flush=True)
    tail = (base / "terminal.log").read_text(errors="replace").splitlines()[-95:]
    print("TERMINAL_TAIL_BEGIN\n" + "\n".join(tail) + "\nTERMINAL_TAIL_END", flush=True)
    print("EVIDENCE_DIR=" + str(base), flush=True)
    return 124 if timed_out else exit_code

if __name__ == "__main__":
    raise SystemExit(main())
