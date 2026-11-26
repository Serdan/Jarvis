#!/usr/bin/env python3
"""Offline Linux qualification orchestration. Plan by default; never resume/replay."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

# Independent bounded invocations; not parallel. Whole-process timeout rather
# than a 75-second no-output watchdog (generated properties can take minutes).
PROJECTS = [
    ("fixed", "tests/AutomationFixedJoint20261125/AutomationFixedJoint20261125.fsproj", 900),
    ("manual", "tests/AutomationManualBypassWitness20261125/AutomationManualBypassWitness20261125.fsproj", 900),
    ("scheduled", "tests/AutomationScheduledReconcileWitness20261125/AutomationScheduledReconcileWitness20261125.fsproj", 900),
    ("ready-resume", "tests/ScheduledReadyResume20261125/ScheduledReadyResume20261125.fsproj", 900),
    ("hosted", "tests/AutomationHealthHosted20261123/AutomationHealthHosted20261123.fsproj", 900),
    ("health-coupled", "scripts/tests/automation-health-coupled-20261122/HealthCoupled.Tests.fsproj", 900),
    ("roster", "tests/AutomationHealthRoster20261125/AutomationHealthRoster20261125.fsproj", 900),
    ("challenge", "tests/AutomationChallengeWake20261122/AutomationChallengeWake20261122.fsproj", 900),
    ("contention", "tests/AutomationHealthContentionWorker21/Contention.fsproj", 900),
    ("capacity", "tests/AutomationCapacityOperator20261125/AutomationCapacityOperator20261125.fsproj", 900),
    ("capacity-policy", "tests/ProjectCapacityPolicy20261124/ProjectCapacityPolicy20261124.fsproj", 900),
    ("working-count", "tests/ProjectWorkingCount20261125/ProjectWorkingCount20261125.fsproj", 900),
    ("count-reader", "tests/ProjectWorkingCountReader20261125/ProjectWorkingCountReader20261125.fsproj", 900),
    ("policy", "tests/AutomationPolicyPropertiesW20/AutomationPolicyPropertiesW20.fsproj", 900),
    ("provider-isolated", "tests/ProviderLaunch20261126/ProviderLaunch20261126.fsproj", 900),
    ("client", "Client.Tests/Client.Tests.fsproj", 5400),
    ("server", "Server.Tests/Server.Tests.fsproj", 3600),
    ("holistic", "tests/AutomationSystemProperties20261124/AutomationSystemProperties20261124.fsproj", 3600),
]

def sha(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()

def receipt(path, data):
    temp = path.with_suffix(".tmp")
    with temp.open("x", encoding="utf-8") as f:
        json.dump(data, f, indent=2, sort_keys=True)
        f.write("\n")
        f.flush()
        os.fsync(f.fileno())
    os.replace(temp, path)
    fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)

def isolated_env(root):
    # Explicit allowlist; do not inherit provider secrets, GIT_CONFIG_COUNT,
    # GIT_DIR, cloud credentials, runtime configuration, or real HOME.
    env = {k: os.environ[k] for k in ("PATH", "LANG", "LC_ALL", "DOTNET_ROOT")
           if k in os.environ}
    for name in ("home", "tmp", "config", "cache", "data", "packages"):
        (root / name).mkdir()
    gitconfig = root / "home" / ".gitconfig"
    gitconfig.write_text("[user]\n\tname = Jarvis Qualification\n\temail = qualification@example.invalid\n", encoding="utf-8")
    gitconfig.chmod(0o600)
    env.update({
        "HOME": str(root / "home"), "TMPDIR": str(root / "tmp"),
        "XDG_CONFIG_HOME": str(root / "config"), "XDG_CACHE_HOME": str(root / "cache"),
        "XDG_DATA_HOME": str(root / "data"), "DOTNET_CLI_HOME": str(root / "home"),
        "NUGET_PACKAGES": str(root / "packages"), "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1", "GIT_CONFIG_NOSYSTEM": "1",
        "MSBUILDDISABLENODEREUSE": "1",
        "DOTNET_CLI_USE_MSBUILD_SERVER": "0",
        "GIT_CONFIG_GLOBAL": str(root / "home" / ".gitconfig"),
        "GIT_TERMINAL_PROMPT": "0", "GIT_AUTHOR_NAME": "Jarvis Qualification",
        "GIT_AUTHOR_EMAIL": "qualification@example.invalid",
        "GIT_COMMITTER_NAME": "Jarvis Qualification",
        "GIT_COMMITTER_EMAIL": "qualification@example.invalid",
    })
    return env

def checked(args, cwd, env, timeout=300):
    return subprocess.check_output(args, cwd=cwd, env=env, timeout=timeout,
                                   stdin=subprocess.DEVNULL,
                                   stderr=subprocess.STDOUT, text=True).strip()

def command(project, name, result_dir):
    return ["dotnet", "test", project, "-c", "Release",
            "--logger", f"trx;LogFileName={name}.trx",
            "--results-directory", str(result_dir)]

# Supported flat Linux VSTest TRX format observed in the retained qualification
# evidence. This is a candidate predicate, not a generic TRX validator.
VSTEST_COUNTS = {"total", "executed", "passed", "failed", "notExecuted"}
VSTEST_OTHER_OUTCOMES = {
    "error", "timeout", "aborted", "inconclusive", "passedButRunAborted",
    "notRunnable", "disconnected", "warning", "completed", "inProgress", "pending",
}
GROUP_DRAIN_SECONDS = 2
TERMINATE_GRACE_SECONDS = 2
KILL_REAP_SECONDS = 10


def trx_summary(path):
    # Preserve raw TRX; distinguish missing/invalid/contradictory metadata.
    if not path.is_file():
        return {"state": "missing"}
    try:
        root = ET.parse(path).getroot()
        summaries = root.findall("./{*}ResultSummary")
        summary = summaries[0] if len(summaries) == 1 else None
        counter_nodes = summary.findall("./{*}Counters") if summary is not None else []
        counters = counter_nodes[0] if len(counter_nodes) == 1 else None
        rows = root.findall(".//{*}UnitTestResult")
        results = root.findall("./{*}Results")
        flat = (root.tag.rsplit("}", 1)[-1] == "TestRun"
                and len(results) == 1
                and list(results[0]) == rows
                and len(root.findall(".//{*}ResultSummary")) == 1
                and len(root.findall(".//{*}Counters")) == 1)
        outcomes = {}
        for row in rows:
            key = row.attrib.get("outcome", "Missing")
            outcomes[key] = outcomes.get(key, 0) + 1
        return {"state": "parsed" if counters is not None and flat else "invalid",
                "summary": summary.attrib if summary is not None else {},
                "counters": counters.attrib if counters is not None else {},
                "rows": len(rows), "outcomes": outcomes}
    except (ET.ParseError, OSError) as exc:
        return {"state": "invalid", "error": str(exc)}


def consistent_trx(trx):
    # Only Completed runs consisting of Passed and NotExecuted rows are
    # supported for candidate evidence. Skip acceptance is still independent.
    if trx.get("state") != "parsed" or trx["summary"].get("outcome") != "Completed":
        return False
    outcomes = trx["outcomes"]
    counters = trx["counters"]
    if (set(outcomes) - {"Passed", "NotExecuted"}
            or set(counters) != VSTEST_COUNTS | VSTEST_OTHER_OUTCOMES
            or any(not re.fullmatch(r"[0-9]+", value) for value in counters.values())):
        return False
    try:
        counts = {key: int(value) for key, value in counters.items()}
    except ValueError:
        return False
    passed = outcomes.get("Passed", 0)
    skipped = outcomes.get("NotExecuted", 0)
    # Nonzero unsupported outcome counters (including warning) are ambiguous
    # here, not proof of a failed test. Ordinary run log warnings are distinct.
    return (counts["total"] == trx["rows"] == passed + skipped
            and counts["executed"] == counts["passed"] == passed
            and counts["notExecuted"] == skipped
            and counts["failed"] == 0
            and all(counts[key] == 0 for key in VSTEST_OTHER_OUTCOMES))


def group_absence(pgid):
    """Bounded, signal-zero-only observation after reaping the stage leader.

    ESRCH establishes absence of the original process group. A surviving
    zombie, permission error or even a recycled PGID is conservatively
    noncandidate. Never send a killing signal after the leader is reaped.
    """
    deadline = time.monotonic() + GROUP_DRAIN_SECONDS
    while True:
        try:
            os.killpg(pgid, 0)
        except ProcessLookupError:
            return "absent"
        except OSError as exc:
            return f"probe_failed: {exc}"
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return "present_or_unreaped"
        time.sleep(min(0.05, remaining))


def run_stage(name, args, seconds, checkout, env, evidence):
    stage = evidence / name
    stage.mkdir()  # Never overwrite a prior witness or replay unknown work.
    receipt(stage / "started.json", {"argv": args, "deadline_seconds": seconds,
                                     "group_drain_seconds": GROUP_DRAIN_SECONDS,
                                     "terminate_grace_seconds": TERMINATE_GRACE_SECONDS,
                                     "kill_reap_seconds": KILL_REAP_SECONDS,
                                     "started_unix": time.time()})
    log = stage / "console.log"
    timed_out = False
    code = None
    boundary = "leader_not_reaped"
    cleanup_errors = []
    with log.open("xb") as stream:
        try:
            process = subprocess.Popen(args, cwd=checkout, env=env,
                                       stdout=stream, stderr=subprocess.STDOUT,
                                       stdin=subprocess.DEVNULL,
                                       start_new_session=True)
        except OSError as exc:
            receipt(stage / "terminal.json", {"state": "spawn_failed", "error": str(exc)})
            return False
        try:
            code = process.wait(timeout=seconds)
        except subprocess.TimeoutExpired:
            timed_out = True
            # Popen.wait timed out without reaping the leader. Its PID pins
            # the original group identity through both cleanup signals.
            for sig in (signal.SIGTERM, signal.SIGKILL):
                try:
                    os.killpg(process.pid, sig)
                except ProcessLookupError:
                    pass
                except OSError as exc:
                    cleanup_errors.append(str(exc))
                if sig == signal.SIGTERM:
                    time.sleep(TERMINATE_GRACE_SECONDS)
            try:
                code = process.wait(timeout=KILL_REAP_SECONDS)
            except subprocess.TimeoutExpired:
                pass
            except OSError as exc:
                cleanup_errors.append(str(exc))
        except OSError as exc:
            cleanup_errors.append(str(exc))
        if code is not None:
            boundary = group_absence(process.pid)
        stream.flush()
        os.fsync(stream.fileno())

    # No stable manifest or parsed TRX assertion while an original group
    # member may still write. Keep partial raw files and stop the matrix.
    result = {"state": "process_boundary_unknown",
              "exit_code": code, "timed_out": timed_out,
              "process_group": process.pid, "group_observation": boundary,
              "cleanup_errors": cleanup_errors, "finished_unix": time.time()}
    if boundary != "absent":
        receipt(stage / "terminal.json", result)
        return False

    trx = trx_summary(stage / (name + ".trx"))
    files = {str(p.relative_to(stage)): sha(p) for p in sorted(stage.rglob("*"))
             if p.is_file()}
    # Not final acceptance: the reviewer must reconcile all counters, skips,
    # platform exclusions, exact source, and independently verified receipts.
    consistent = consistent_trx(trx)
    result.update({"state": "timeout" if timed_out else "exited",
                   "finished_unix": time.time(), "sha256": files, "trx": trx,
                   "trx_counters_consistent": consistent})
    receipt(stage / "terminal.json", result)
    return not timed_out and code == 0 and consistent and trx["outcomes"].get("Passed", 0) > 0

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--source", type=Path, required=True, help="local source repository")
    p.add_argument("--commit", required=True, help="exact approved commit SHA, not a ref")
    p.add_argument("--output", type=Path, required=True, help="new persistent directory outside source")
    p.add_argument("--execute", action="store_true", help="only after separate Operator approval")
    p.add_argument("--approval", help="record external approval ID (not an authorization mechanism)")
    a = p.parse_args()
    if not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", a.commit):
        p.error("commit must be a full lowercase SHA")
    source, output = a.source.resolve(), a.output.resolve()
    if output.is_relative_to(source) or source.is_relative_to(output):
        p.error("output must be independent of source")
    matrix = [{"name": name, "project": proj, "deadline_seconds": seconds}
              for name, proj, seconds in PROJECTS]
    if not a.execute:
        print(json.dumps({"commit": a.commit, "matrix": matrix}, indent=2))
        return 0
    if not a.approval or not a.approval.strip():
        p.error("--execute requires a separately obtained approval reference")
    output.mkdir(mode=0o700)  # Existing run is never resumed.
    env = isolated_env(output)
    checkout = output / "checkout"
    evidence = output / "evidence"
    evidence.mkdir()
    receipt(evidence / "run-started.json",
            {"commit": a.commit, "approval_reference": a.approval, "matrix": matrix,
             "runner_sha256": sha(Path(__file__).resolve())})
    checked(["git", "clone", "--no-local", "--no-checkout", "--", str(source),
             str(checkout)], output, env)
    checked(["git", "checkout", "--detach", a.commit], checkout, env)
    if checked(["git", "rev-parse", "HEAD"], checkout, env) != a.commit:
        raise RuntimeError("source mismatch")
    if checked(["git", "status", "--porcelain"], checkout, env):
        raise RuntimeError("checkout is not clean")
    for _, project, _ in PROJECTS:
        if not (checkout / project).is_file():
            raise RuntimeError("missing matrix project: " + project)
    for role in ("GIT_AUTHOR_IDENT", "GIT_COMMITTER_IDENT"):
        checked(["git", "var", role], checkout, env)
    sdk = checked(["dotnet", "--version"], checkout, env)
    if not sdk.startswith("11."):
        raise RuntimeError("expected .NET 11 SDK")
    receipt(evidence / "source.json",
            {"commit": a.commit, "tree": checked(["git", "rev-parse", "HEAD^{tree}"],
                                                 checkout, env),
             "sdk": sdk, "platform": sys.platform,
             "dotnet_info": checked(["dotnet", "--info"], checkout, env)})
    results = {}
    for name, project, seconds in PROJECTS:
        results[name] = run_stage(name, command(project, name, evidence / name),
                                  seconds, checkout, env, evidence)
        stage_terminal = json.loads((evidence / name / "terminal.json").read_text())
        if stage_terminal["state"] != "exited":
            break  # Never overlap a later stage with an uncertain process.
        if (checked(["git", "rev-parse", "HEAD"], checkout, env) != a.commit or
                checked(["git", "status", "--porcelain", "--untracked-files=no"], checkout, env)):
            results[name] = False
            break
    candidate = len(results) == len(PROJECTS) and all(results.values())
    receipt(evidence / "run-terminal.json",
            {"stage_observations": results,
             "state": "review_required" if candidate else "red_or_incomplete"})
    return 0 if candidate else 1

if __name__ == "__main__":
    sys.exit(main())
