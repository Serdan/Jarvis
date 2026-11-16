#!/usr/bin/env python3
"""WQ10 operator-facing, OFFLINE-only two-project rehearsal.

Consumes the accepted C# migration preview and captured WQ08 read-only host
preview JSON. It never writes a Git repo, contacts the Jarvis server or calls
any Scheduler/Manager/browser action. Input evidence is operator supplied, not
an independently authenticated protocol receipt.
"""
import json
import pathlib
import subprocess
import sys


ROOT = pathlib.Path(__file__).resolve().parents[2]
DLL = ROOT / "tools/Wq10MigrationPreview/bin/Release/net11.0/Wq10MigrationPreview.dll"
EXPECTED_PREVIEW_ISSUES = {"AccountNotActivated"}


def load(path):
    with path.open(encoding="utf-8") as handle:
        return json.load(handle)


def resolved(base, file_name):
    if not isinstance(file_name, str) or not file_name.strip():
        raise ValueError("Rehearsal requires explicit local JSON file paths")
    return (base / file_name).resolve()


def normalize_preview_keys(value):
    # The WQ08 host CLI uses default System.Text.Json PascalCase fields;
    # fixture exporters and some operator scripts emit camelCase instead.
    if isinstance(value, list):
        return [normalize_preview_keys(item) for item in value]
    if isinstance(value, dict):
        return {key[:1].lower() + key[1:]: normalize_preview_keys(item)
                for key, item in value.items()}
    return value


def fail(code, context, next_action):
    return {"code": code, "context": context, "nextAction": next_action}


def review_case(case, base, app=DLL):
    name = case["name"]
    manifest_path = resolved(base, case["manifest"])
    manifest = load(manifest_path)
    if manifest.get("schemaVersion") != 1:
        raise ValueError(f"{name}: expected migration manifest schemaVersion 1")
    supplied = manifest.get("projects")
    if not isinstance(supplied, list) or len(supplied) != 2:
        raise ValueError(f"{name}: rehearsal requires exactly two distinct project snapshots")
    if len({p.get("name") for p in supplied}) != 2:
        raise ValueError(f"{name}: project names must be distinct")
    previews = case.get("schedulerPreviews")
    if not isinstance(previews, dict):
        raise ValueError(f"{name}: schedulerPreviews must provide one WQ08 JSON path per project")
    if set(previews) != {p["name"] for p in supplied}:
        raise ValueError(f"{name}: Scheduler preview project names must exactly match manifest")
    result = subprocess.run(["dotnet", str(app), str(manifest_path)],
                            text=True, capture_output=True, timeout=90, check=False)
    if result.returncode:
        raise RuntimeError(f"{name}: original WQ10 inspector failed: {result.stderr.strip()}")
    report = json.loads(result.stdout)
    account = report.get("account")
    if not account:
        raise ValueError(f"{name}: account inventory missing from WQ10 report")

    issues = []
    matrix = []
    for project in report["projects"]:
        project_name = project["name"]
        preview = normalize_preview_keys(load(resolved(base, previews[project_name])))
        if not isinstance(preview, dict):
            raise ValueError(f"{name}/{project_name}: WQ08 preview must be a JSON object")
        prefix = f"{name}/{project_name}"
        def check(ok, code, context, action):
            if not ok:
                issues.append(fail(code, f"{prefix}: {context}", action))

        check(preview.get("mode") == "ReadOnlyDryRun" and preview.get("canDispatch") is False,
              "SCHEDULER_NOT_INERT", "mode/canDispatch does not match WQ08 read-only contract",
              "Capture a genuine default-off WQ08 host preview; never authorize dispatch here")
        check(preview.get("projectHead") == project["head"] == project["currentRefTip"],
              "PROJECT_HEAD_CONFLICT", f"pinned {project['head']}; observed {project['currentRefTip']}; scheduler {preview.get('projectHead')}",
              "Pin and independently fetch the same exact project Git head again")
        check(preview.get("accountHead") == account["head"] == account["currentRefTip"],
              "ACCOUNT_HEAD_CONFLICT", f"pinned {account['head']}; observed {account['currentRefTip']}; scheduler {preview.get('accountHead')}",
              "Reconcile account token owners and competing writer before a fresh preview")
        capacity = account["maxConcurrentAgents"]
        occupied = account["heldTokens"]
        check(preview.get("maxConcurrentAgents") == capacity and
              preview.get("occupiedAgents") == occupied and
              preview.get("availableSlots") == max(0, capacity - occupied),
              "CAPACITY_EVIDENCE_CONFLICT",
              f"inspector cap={capacity} held={occupied}; scheduler cap={preview.get('maxConcurrentAgents')} held={preview.get('occupiedAgents')} free={preview.get('availableSlots')}",
              "Fetch both account observations at exact heads; do not infer capacity")
        check(isinstance(preview.get("roleId"), str) and bool(preview.get("roleId")) and
              isinstance(preview.get("selectedRoleVersion"), int) and
              preview["selectedRoleVersion"] > 0,
              "ROLE_PREVIEW_UNVERIFIED", "enabled versioned role not selected",
              "Inspect WQ04 enabled role definition at pinned project Git head")
        candidate = preview.get("wouldLaunch")
        if case.get("requireCandidate", True):
            check(isinstance(candidate, dict) and
                  all(candidate.get(key) for key in ("workId", "sourceOperationId",
                                                     "sourceRequestHash", "originBlobOid")),
                  "SOURCE_PROVENANCE_UNVERIFIED", "positive candidate lacks original identity or source receipt",
                  "Read the immutable WQ04 work origin; do not invent a source UUID or hash")
        preview_issues = preview.get("issues")
        if not isinstance(preview_issues, list):
            issues.append(fail("SCHEDULER_ISSUES_MISSING", prefix,
                               "Capture the full WQ08 issue inventory, even when empty"))
            preview_issues = []
        for item in preview_issues:
            code = item.get("code") if isinstance(item, dict) else None
            if not code or code not in EXPECTED_PREVIEW_ISSUES:
                issues.append(fail("SCHEDULER_PREVIEW_ISSUE", f"{prefix}: {code or 'malformed'}",
                                   item.get("nextAction", "Inspect original WQ08 issue") if isinstance(item, dict)
                                   else "Inspect exact Scheduler issue"))
        matrix.append({
            "project": project_name,
            "pinnedHead": project["head"],
            "observedHead": project["currentRefTip"],
            "queueMigration": project["queueMigration"],
            "legacyTasks": [t["taskId"] for t in project["tasks"]],
            "pendingMessageIds": [m["messageId"] for m in project["messages"]],
            "workingAgents": [a["agentId"] for a in project["agents"] if a["execution"] == "Working"],
            "launchIds": [launch["launchId"] for launch in project["launches"]],
            "scheduler": {"mode": preview.get("mode"), "roleId": preview.get("roleId"),
                          "selectedRoleVersion": preview.get("selectedRoleVersion"),
                          "wouldLaunch": candidate.get("workId") if isinstance(candidate, dict) else None,
                          "sourceOperationId": candidate.get("sourceOperationId") if isinstance(candidate, dict) else None,
                          "sourceRequestHash": candidate.get("sourceRequestHash") if isinstance(candidate, dict) else None,
                          "originBlobOid": candidate.get("originBlobOid") if isinstance(candidate, dict) else None,
                          "canDispatch": preview.get("canDispatch"),
                          "issues": [i.get("code", "malformed") if isinstance(i, dict) else "malformed"
                                     for i in preview_issues]}
        })

    original_ids = [{"kind": "pendingGitOperation", **p} for p in report["pendingOperations"]]
    original_ids += [{"kind": "browserEffect", "id": item}
                     for item in report["uncertainBrowserSubmissions"]]
    original_ids += [{"kind": "sourceJob", "id": item}
                     for item in report["inFlightSourceJobs"]]
    observations = manifest.get("observations") or {}
    fences = {key: observations.get(key) is True for key in
              ("legacyManagerStopped", "oldWritersFenced", "clientProtocolsCompatible")}
    inspection_blockers = [f for f in report["findings"] if f["severity"] == "BLOCKER"]
    all_blockers = [{"code": f["code"], "context": f["context"], "nextAction": f["nextAction"]}
                    for f in inspection_blockers] + issues
    prepared = report["readiness"] == "PREVIEW_READY_FOR_REVIEW" and not all_blockers
    snapshots = [{"authority": "account", "head": account["head"],
                  "observedHead": account["currentRefTip"],
                  "repo": manifest["account"]["repo"], "ref": manifest["account"]["ref"]}]
    for entry in sorted(supplied, key=lambda e: e["name"]):
        inventory = next(p for p in report["projects"] if p["name"] == entry["name"])
        snapshots.append({"authority": entry["name"], "head": inventory["head"],
                          "observedHead": inventory["currentRefTip"],
                          "repo": entry["repo"], "ref": entry["ref"]})
    return {
        "name": name,
        "rehearsalVerdict": "PREPARED_FOR_REVIEW" if prepared else "NO_GO",
        "productionCutover": "NOT_AUTHORIZED",
        "account": {"head": account["head"], "migrationPhase": account["migrationPhase"],
                    "cap": account["maxConcurrentAgents"], "held": account["heldTokens"],
                    "heldLaunchIds": account["heldLaunchIds"]},
        "writerFences": fences,
        "projectMatrix": matrix,
        "originalUncertainIds": original_ids,
        "blockers": all_blockers,
        "snapshots": snapshots,
        "manualRepairAndRollback": [
            "Before authorized mutations, create independently restorable Git bundles for every listed exact head and retain full original operation/launch/browser IDs and actors.",
            "Diff each pinned Git head against its observed ref and separately cross-check source, browser, admission and terminal receipts; an absent receipt is not a negative outcome.",
            "Record an operator-approved minimal repair diff, actor, old/new head and original-ID receipt; only nonforce compare-and-swap against the inspected head is permissible.",
            "On any concurrent head change or lost acknowledgment, stop; inspect the original receipt and reconstruct from the saved immutable heads. Never force-push, reissue unknown browser effects or release capacity without terminal evidence.",
            "Only a separate, explicit operator-approved epoch/authority transition may fence old clients, retire Manager or activate Scheduler. This rehearsal never does so."
        ],
        "evidenceBoundary": "Operator-supplied pinned offline Git snapshots and captured WQ08 read-only JSON; not live, independently authenticated cutover authorization"
    }


def markdown(out):
    lines = ["# WQ10 two-project offline cutover rehearsal",
             "", "**Production cutover: NOT AUTHORIZED** — no Manager retirement, Scheduler activation, epoch change, reservation, browser submission or Git mutation.",
             "", "Imported WQ08 JSON and writer-fence observations are operator-supplied evidence; the CLI does not authenticate their capture.",
             ""]
    for case in out["cases"]:
        lines += [f"## {case['name']}: {case['rehearsalVerdict']}", "",
                  f"Account Git head: `{case['account']['head']}` | migration: {case['account']['migrationPhase']} | cap: {case['account']['cap']} | held: {case['account']['held']} | original held launch IDs: {', '.join(case['account']['heldLaunchIds']) or '(none)'}",
                  "",
                  "### Go / No-Go matrix",
                  "| Gate | Finding | Verdict |",
                  "|---|---|---|"]
        for key, value in case["writerFences"].items():
            lines.append(f"| {key} | Operator-supplied: {str(value).lower()} | {'EVIDENCED' if value else 'NO_GO'} |")
        for p in case["projectMatrix"]:
            issues = ", ".join(p["scheduler"]["issues"]) or "none"
            lines.append(f"| {p['project']} immutable provenance | source UUID: {p['scheduler']['sourceOperationId'] or '(missing)'}; request SHA: {p['scheduler']['sourceRequestHash'] or '(missing)'}; origin blob: {p['scheduler']['originBlobOid'] or '(missing)'} | REVIEW |")
            lines.append(f"| {p['project']} migration / Scheduler | pinned `{p['pinnedHead']}`; observed `{p['observedHead']}`; {p['queueMigration']}; WQ08 {p['scheduler']['mode']}, role {p['scheduler']['roleId']} v{p['scheduler']['selectedRoleVersion']}, candidate {p['scheduler']['wouldLaunch'] or '(none)'}, issues: {issues}; dispatch={p['scheduler']['canDispatch']} | {'INSPECT' if issues else 'EVIDENCED'} |")
            lines.append(f"| {p['project']} legacy state | tasks: {', '.join(p['legacyTasks']) or '(none)'}; messages: {', '.join(p['pendingMessageIds']) or '(none)'}; working: {', '.join(p['workingAgents']) or '(none)'}; launches: {', '.join(p['launchIds']) or '(none)'} | INVENTORIED |")
        lines += ["", "### Pending / uncertain originals"]
        if case["originalUncertainIds"]:
            for op in case["originalUncertainIds"]:
                lines.append(f"- {op['kind']}: `{op['id']}` (project {op.get('project', 'n/a')}, actor {op.get('actor', 'n/a')}, disposition {op.get('status', 'unknown')})")
        else:
            lines.append("- All evidence inventories explicitly empty in supplied manifest (not an independently verified absence).")
        lines += ["", "### No-Go findings"]
        if case["blockers"]:
            for f in case["blockers"]:
                lines.append(f"- **{f['code']}** — {f['context']}. Action: {f['nextAction']}.")
        else:
            lines.append("- No blockers in supplied offline evidence; this is a *rehearsal-prepared* result, not permission to cut over.")
        lines += ["", "### Exact-head backup / diff / receipts"]
        for row in case["snapshots"]:
            lines.append(f"- **{row['authority']}** `{row['ref']}`: pinned `{row['head']}`, observed `{row['observedHead']}`, local clone `{row['repo']}`.")
        for step in case["manualRepairAndRollback"]:
            lines.append("- " + step)
        lines.append("")
    return "\n".join(lines) + "\n"


def main(argv):
    if len(argv) not in (2, 4) or (len(argv) == 4 and
                                      (argv[2] != "--format" or argv[3] not in ("json", "markdown"))):
        print("Usage: python3 tools/Wq10MigrationPreview/rehearsal.py <rehearsal.json> [--format json|markdown]",
              file=sys.stderr)
        return 64
    try:
        path = pathlib.Path(argv[1]).resolve()
        data = load(path)
        if data.get("schemaVersion") != 1 or not isinstance(data.get("cases"), list) or not data["cases"]:
            raise ValueError("Requires schemaVersion=1 and nonempty cases")
        names = [case["name"] for case in data["cases"]]
        if len(names) != len(set(names)):
            raise ValueError("Case names must be distinct")
        report = {"schemaVersion": 1, "mode": "OFFLINE_READ_ONLY",
                  "productionCutover": "NOT_AUTHORIZED",
                  "cases": [review_case(case, path.parent) for case in data["cases"]]}
        if len(argv) == 4 and argv[3] == "markdown":
            sys.stdout.write(markdown(report))
        else:
            print(json.dumps(report, indent=2, sort_keys=True))
        return 0
    except (ValueError, KeyError, OSError, RuntimeError, json.JSONDecodeError,
            subprocess.TimeoutExpired) as ex:
        print(f"Invalid WQ10 rehearsal input: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
