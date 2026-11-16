#!/usr/bin/env python3
"""WQ10 fixture-only prepared-state adapter. No production migration entry point.

This *cannot* activate Work, reserve tokens, alter a legacy authority ref, or
publish canonical WQ02/WQ07 state. It stages an explicitly non-runnable typed
migration plan in disposable Git authorities for audited follow-up.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import uuid

HERE = Path(__file__).resolve().parent
INSPECTOR = HERE / "bin/Release/net11.0/Wq10MigrationPreview.dll"
SENTINEL = ".wq10-disposable-fixture"
SENTINEL_VALUE = "WQ10_DISPOSABLE_FIXTURE_V1"
REF_PREFIX = "refs/heads/wq10-prepared/"
MISSING_BINDINGS = (
    "WQ07_ACCOUNT_MIGRATION_PREPARE_CAS_AND_RECEIPT",
    "WQ02_TYPED_PROJECT_PREPARED_TRANSITION",
    "ORIGINAL_ID_WORK_ORIGIN_AND_ROLE_MAPPING",
    "HARD_DEPENDENCY_AND_FIFO_PRODUCER_EVIDENCE",
    "REVIEWED_PREFLIGHT_AUTHENTICATION",
)


class PreparationError(Exception):
    def __init__(self, code: str, next_action: str):
        super().__init__(f"{code}: {next_action}")
        self.code = code
        self.next_action = next_action


def fail(code: str, action: str) -> None:
    raise PreparationError(code, action)


def canonical(value: object) -> str:
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":"))


def pretty(value: object) -> str:
    return json.dumps(value, sort_keys=True, ensure_ascii=False, indent=2) + "\n"


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def run_git(repo: Path | None, *args: str, input_text: str | None = None,
            check: bool = True, env_extra: dict | None = None) -> subprocess.CompletedProcess:
    command = ["git"]
    if repo is not None:
        command.extend(("-C", str(repo)))
    command.extend(args)
    env = os.environ.copy()
    for key in ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
                "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM"):
        env.pop(key, None)
    env.update({"GIT_TERMINAL_PROMPT": "0", "GIT_CONFIG_NOSYSTEM": "1",
                "GIT_CONFIG_GLOBAL": os.devnull})
    if env_extra:
        env.update(env_extra)
    try:
        result = subprocess.run(command, input=input_text, text=True, capture_output=True,
                                timeout=30, check=False, env=env)
    except (OSError, subprocess.TimeoutExpired) as exc:
        fail("GIT_UNCERTAIN", f"Inspect the fixture Git process and original refs: {exc}")
    if check and result.returncode:
        fail("GIT_FAILED", f"{' '.join(command[:4])}: {result.stderr.strip()}; "
                           "inspect the exact ref and original operation ID; do not force-update")
    return result


def ref_tip(repo: Path, ref: str) -> str | None:
    result = run_git(repo, "rev-parse", "--verify", "--quiet", ref, check=False)
    if result.returncode == 1:
        return None
    if result.returncode:
        fail("REF_UNREADABLE", f"Inspect {ref} before continuing")
    return result.stdout.strip()


def disposable_root(path: str) -> Path:
    root = Path(path).resolve(strict=True)
    temp = Path(tempfile.gettempdir()).resolve(strict=True)
    if root == temp or temp not in root.parents:
        fail("NOT_DISPOSABLE_ROOT", "Use a dedicated directory beneath the OS temporary directory")
    if not root.is_dir() or (root / SENTINEL).is_symlink() or (
            root / SENTINEL).read_text(encoding="utf-8").strip() != SENTINEL_VALUE:
        fail("FIXTURE_MARKER_MISSING",
             f"Create an explicitly disposable root with {SENTINEL} containing {SENTINEL_VALUE}")
    return root


def validate_repo(root: Path, input_repo: str, used: set[Path]) -> Path:
    repo = Path(input_repo).resolve(strict=True)
    if root not in repo.parents or not repo.is_dir() or repo in used:
        fail("FIXTURE_BOUNDARY_INVALID", "All authorities must be distinct bare Git repositories under fixture root")
    if run_git(repo, "rev-parse", "--is-bare-repository").stdout.strip() != "true":
        fail("NOT_BARE_FIXTURE", "Use disposable bare Git authority clones only")
    if run_git(repo, "remote").stdout.strip():
        fail("EXTERNAL_REMOTE_CONFIGURED",
             "Remove the fixture remote binding; WQ10 must not access network authorities")
    used.add(repo)
    return repo


def operation_directory(root: Path, operation_id: str) -> Path:
    """Do not follow symlinks in the fixture evidence write path."""
    parent = root / "wq10-preparations"
    work = parent / operation_id
    if (parent.is_symlink() or work.is_symlink()
            or work.resolve() != work or root not in work.parents):
        fail("FIXTURE_WRITE_BOUNDARY_INVALID",
             "Remove the symlink or escaped preparation directory; preserve existing evidence")
    return work


def checked_output(work: Path, filename: str) -> Path:
    """Authorize only fixed evidence filenames wholly inside the operation directory."""
    if not re.fullmatch(r"(?:account|project-[12])\.(?:bundle|diff)|receipt\.(?:json|tmp)", filename):
        fail("FIXTURE_WRITE_BOUNDARY_INVALID", "Use only canonical authority evidence filenames")
    path = work / filename
    if (work.resolve() != work or work not in path.parents
            or path.is_symlink() or path.resolve() != path):
        fail("FIXTURE_WRITE_BOUNDARY_INVALID",
             f"Preserve unexpected evidence or symlink at {path}; never escape fixture operation")
    return path


def verified_report(manifest_path: Path, report_path: Path, reviewed_sha: str) -> tuple[dict, dict, str]:
    raw = report_path.read_bytes()
    actual_sha = digest(raw)
    if not re.fullmatch(r"[0-9a-f]{64}", reviewed_sha) or actual_sha != reviewed_sha:
        fail("REVIEWED_REPORT_PIN_MISMATCH",
             "Supply the independently reviewed exact report file SHA-256, not a modified report")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    report = json.loads(raw)
    if not INSPECTOR.is_file():
        fail("INSPECTOR_NOT_BUILT", "Build the accepted WQ10 .NET 11 Release inspector")
    command = subprocess.run(["dotnet", str(INSPECTOR), str(manifest_path)],
                             capture_output=True, text=True, timeout=90, check=False)
    if command.returncode:
        fail("PREFLIGHT_RECHECK_FAILED", "Review the current pinned Git state: " + command.stderr.strip())
    if json.loads(command.stdout) != report:
        fail("PREFLIGHT_REPORT_CHANGED",
             "Regenerate/review a fresh exact preflight; do not reuse edited or stale evidence")
    if (report.get("mode") != "OFFLINE_READ_ONLY"
            or report.get("activation") != "NOT_AUTHORIZED"
            or report.get("readiness") != "PREVIEW_READY_FOR_REVIEW"):
        fail("PREFLIGHT_NOT_READY",
             "Resolve the WQ10 blockers at their original IDs; preparation does not override NO_GO")
    if len(manifest.get("projects", [])) != 2:
        fail("TWO_PROJECT_FIXTURE_REQUIRED", "Supply exactly two distinct disposable project authorities")
    if any(f.get("severity") == "BLOCKER" for f in report.get("findings", [])):
        fail("PREFLIGHT_BLOCKED", "Resolve blockers without discarding historical evidence")
    if report["account"]["migrationPhase"] not in ("Frozen", "LegacyActive"):
        fail("ACCOUNT_PHASE_UNSAFE", "Use a frozen legacy fixture, never validated or activated authority")
    if any(p["queueMigration"] not in ("", "Frozen") for p in report["projects"]):
        fail("PROJECT_PHASE_UNSAFE", "Inspect the prior canonical prepared receipt; do not repeat cutover")
    return manifest, report, actual_sha


def validate_project_lineage(project: dict) -> None:
    for task in project["tasks"]:
        if task["disposition"] == "PROPOSE":
            if not task["sourceRefs"] or not task["sourceCommits"] or not task["attempts"]:
                fail("SOURCE_LINEAGE_MISSING",
                     f"{project['name']}/{task['taskId']}: recover the original attempt/ref/commit "
                     "and WQ04 origin receipt before deriving a Work stage")
            if any(not re.fullmatch(r"[a-f0-9]{40}([a-f0-9]{24})?", c)
                   for c in task["sourceCommits"]):
                fail("SOURCE_LINEAGE_INVALID",
                     f"{project['name']}/{task['taskId']}: recover exact source Git SHAs")


def project_sidecar(project: dict, operation_id: str, report_sha: str, input_sha: str) -> dict:
    validate_project_lineage(project)
    tasks = []
    for task in sorted(project["tasks"], key=lambda entry: entry["taskId"]):
        tasks.append({
            "legacyTaskId": task["taskId"],
            "legacyTaskPath": task["historicalPath"],
            "legacyAttemptPaths": task["attempts"],
            "originalSourceRefs": task["sourceRefs"],
            "originalSourceCommits": task["sourceCommits"],
            "existingCanonicalStages": task["existingStages"],
            "disposition": task["disposition"],
            "proposedWorkId": task["proposedWorkId"] if task["disposition"] == "PROPOSE" else None,
            "proposedKind": "Implementation" if task["disposition"] == "PROPOSE" else None,
            "proposedState": "Blocked" if task["disposition"] == "PROPOSE" else None,
            "roleId": None,
            "stageGeneration": None,
            "dependencies": None,
            "sourceOriginReceipt": None,
            "runnable": False,
        })
    return {
        "schemaVersion": 1, "kind": "WQ10ProjectAdapterOnly",
        "operationId": operation_id, "inputSha256": input_sha,
        "reviewedReportSha256": report_sha,
        "projectName": project["name"], "legacyHead": project["head"],
        "originalRefTip": project["currentRefTip"],
        "projectLedgerEpochUnchanged": project["ledgerEpoch"],
        "legacyHistoryImmutable": True,
        "proposedStages": tasks,
        "legacyAgents": project["agents"],
        "legacyReviews": project["reviews"],
        "legacyIntegrations": project["integrations"],
        "legacyPendingMessages": project["messages"],
        "originalLaunches": project["launches"],
        "newRoleBindings": [], "newLaunches": [], "newClaims": [],
        "dependencyCoverage": "UNVERIFIED_NOT_EMPTY",
        "canonicalProjectMutation": False, "schedulerEnabled": False,
        "migrationPrepared": False, "productionCutover": "NOT_AUTHORIZED",
        "missingBindings": list(MISSING_BINDINGS),
    }


def account_sidecar(account: dict, operation_id: str, report_sha: str,
                    input_sha: str, projects: list[dict]) -> dict:
    return {
        "schemaVersion": 1, "kind": "WQ10AccountAdapterOnly",
        "operationId": operation_id, "inputSha256": input_sha,
        "reviewedReportSha256": report_sha,
        "legacyHead": account["head"], "originalRefTip": account["currentRefTip"],
        "accountEpochUnchanged": account["epoch"],
        "migrationPhaseUnchanged": account["migrationPhase"],
        "proposedPhaseNotApplied": "Preparing",
        "originalHeldLaunchIds": account["heldLaunchIds"],
        "preparedProjects": [
            {"name": p["name"], "originalHead": p["head"],
             "preparedRef": REF_PREFIX + operation_id, "preparedHead": None,
             "canonicalPrepared": False}
            for p in sorted(projects, key=lambda item: item["name"])],
        "allocatedTokens": [], "migrationPrepared": False,
        "schedulerEnabled": False, "productionCutover": "NOT_AUTHORIZED",
        "missingBindings": list(MISSING_BINDINGS),
    }


def checked_stage_target(checkout: Path, relpath: str) -> Path:
    """Never follow record-path symlinks inherited from an untrusted Git tree.

    The checkout is private, but its contents come from a caller-supplied bare
    repository. Validate each component *before* creating the next directory;
    checking the final path or relying on git add happens too late.
    """
    parts = relpath.split("/")
    if (not parts or any(part in ("", ".", "..") for part in parts)
            or "\\" in relpath or "\0" in relpath or Path(relpath).is_absolute()):
        fail("STAGED_PATH_INVALID", "Use a canonical relative adapter record path")
    boundary = checkout.resolve(strict=True)
    parent = checkout
    for component in parts[:-1]:
        parent = parent / component
        if parent.is_symlink():
            fail("STAGED_PATH_SYMLINK",
                 f"Refuse checked-out symlink at {parent.relative_to(checkout)}")
        if parent.exists():
            if not parent.is_dir():
                fail("ADAPTER_PATH_COLLISION",
                     f"Non-directory component in fixture record path: {relpath}")
        else:
            parent.mkdir()
        if parent.is_symlink() or not parent.resolve(strict=True).is_relative_to(boundary):
            fail("STAGED_PATH_SYMLINK",
                 f"Refuse escaped checked-out path at {parent.relative_to(checkout)}")
    target = parent / parts[-1]
    if target.is_symlink() or not target.resolve().is_relative_to(boundary):
        fail("STAGED_PATH_SYMLINK", f"Refuse checked-out symlink at {relpath}")
    if target.exists():
        fail("ADAPTER_PATH_COLLISION", f"Unexpected existing fixture path: {relpath}")
    return target


def stage_commit(root: Path, repo: Path, head: str, relpath: str, record: dict) -> str:
    # Commit-tree in a private clone; import only objects, then CAS a NEW ref.
    with tempfile.TemporaryDirectory(prefix="wq10-stage-", dir=root) as tmp:
        checkout = Path(tmp) / "checkout"
        run_git(None, "clone", "--quiet", "--no-checkout", str(repo), str(checkout))
        run_git(checkout, "checkout", "--quiet", "--detach", head)
        target = checked_stage_target(checkout, relpath)
        # Exclusive creation refuses a final symlink appearing after validation.
        with target.open("x", encoding="utf-8") as output:
            output.write(pretty(record))
        run_git(checkout, "add", "--", relpath)
        environment = {
            "GIT_AUTHOR_NAME": "WQ10 fixture",
            "GIT_AUTHOR_EMAIL": "wq10@invalid.example",
            "GIT_COMMITTER_NAME": "WQ10 fixture",
            "GIT_COMMITTER_EMAIL": "wq10@invalid.example",
            "GIT_AUTHOR_DATE": "2001-01-01T00:00:00 +0000",
            "GIT_COMMITTER_DATE": "2001-01-01T00:00:00 +0000",
        }
        run_git(checkout, "commit", "--quiet", "-m",
                f"WQ10 fixture-only adapter {record['operationId']}", env_extra=environment)
        sha = run_git(checkout, "rev-parse", "HEAD").stdout.strip()
        # No remote or authority ref is changed by this object-only import.
        run_git(repo, "fetch", "--quiet", "--no-tags", "--no-write-fetch-head",
                str(checkout), "HEAD")
        if run_git(repo, "rev-parse", sha + "^").stdout.strip() != head:
            fail("STAGED_PARENT_MISMATCH", "Inspect the fixture commit ancestry and original Git ref")
        return sha


def cas_create(repo: Path, original_ref: str, original_head: str,
               staging_ref: str, sha: str) -> None:
    tx = (f"start\nverify {original_ref} {original_head}\n"
          f"create {staging_ref} {sha}\nprepare\ncommit\n")
    result = run_git(repo, "update-ref", "--stdin", input_text=tx, check=False)
    if result.returncode:
        fail("COMPETING_WRITER_OR_PREPARATION",
             f"Git CAS rejected {original_ref}; inspect the competing head and original "
             "operation receipt, then abort the partial fixture staging; never force-push")


def atomic_json(path: Path, value: dict) -> None:
    path = checked_output(path.parent, path.name)
    candidate = checked_output(path.parent, path.with_suffix(".tmp").name)
    candidate.write_text(pretty(value), encoding="utf-8")
    os.replace(candidate, path)


def make_specs(root: Path, manifest: dict, report: dict,
               operation_id: str, report_sha: str, input_sha: str) -> list[dict]:
    used: set[Path] = set()
    names = [item.get("name") for item in manifest["projects"]]
    if any(not isinstance(name, str) or not name or name != name.strip()
           or name.casefold() == "account" or name in (".", "..")
           or any(character in name for character in ("/", "\\", "\0"))
           or any(ord(character) < 32 for character in name)
           for name in names):
        fail("UNSAFE_PROJECT_NAME",
             "Use nonblank project names without paths, controls, or the reserved account identity")
    if len({name.casefold() for name in names}) != 2:
        fail("PROJECT_IDENTITY_DUPLICATED", "Use two distinct project identities")
    by_name = {p["name"]: p for p in report["projects"]}
    if len(by_name) != 2:
        fail("PROJECT_IDENTITY_DUPLICATED", "Review the unique pinned project identities")
    specs = []
    for index, item in enumerate(sorted(manifest["projects"], key=lambda entry: entry["name"]), 1):
        name = item["name"]
        if name not in by_name or item["head"] != by_name[name]["head"]:
            fail("PREFLIGHT_PROJECT_MISMATCH", "Reconcile the exact project identity and pinned SHA")
        repo = validate_repo(root, item["repo"], used)
        sidecar = project_sidecar(by_name[name], operation_id, report_sha, input_sha)
        specs.append({"name": name, "repo": str(repo), "originalRef": item["ref"],
                      "authorityKey": f"project-{index}",
                      "originalHead": item["head"],
                      "path": f"records/work-queue/prepared-adapter/{operation_id}.json",
                      "record": sidecar})
    item = manifest["account"]
    if item["head"] != report["account"]["head"]:
        fail("PREFLIGHT_ACCOUNT_MISMATCH", "Reconcile exact account SHA")
    repo = validate_repo(root, item["repo"], used)
    specs.append({"name": "account", "repo": str(repo), "originalRef": item["ref"],
                  "authorityKey": "account",
                  "originalHead": item["head"],
                  "path": f"records/wq10-prepared-adapter/{operation_id}.json",
                  "record": account_sidecar(report["account"], operation_id,
                                            report_sha, input_sha, report["projects"])})
    for spec in specs:
        repo = Path(spec["repo"])
        ref = spec["originalRef"]
        if not re.fullmatch(r"refs/heads/[A-Za-z0-9][A-Za-z0-9._/-]*", ref):
            fail("UNSAFE_REF", "Provide exact approved refs/heads/... from the pinned preflight")
        if ref_tip(repo, ref) != spec["originalHead"]:
            fail("COMPETING_WRITER_HEAD_CHANGED",
                 f"{spec['name']} {ref}: restore and review the exact new head before preparing")
        other_refs = run_git(repo, "for-each-ref", "--format=%(refname)", REF_PREFIX)
        if any(line != REF_PREFIX + operation_id for line in other_refs.stdout.splitlines()):
            fail("OTHER_PREPARATION_PRESENT",
                 f"{spec['name']}: reconcile/abort the other prepared fixture ref first")
    return specs


def prepare(args: argparse.Namespace) -> dict:
    if not args.apply_disposable:
        fail("DISABLED_BY_DEFAULT", "Explicitly pass --apply-disposable for synthetic fixtures only")
    root = disposable_root(args.disposable_root)
    operation_id = str(uuid.UUID(args.operation_id))
    if operation_id != args.operation_id:
        fail("INVALID_OPERATION_ID", "Use the original canonical lowercase UUID, never invent a retry ID")
    manifest, report, report_sha = verified_report(
        Path(args.manifest), Path(args.report), args.reviewed_sha256)
    input_sha = digest(canonical({"manifest": manifest, "report": report}).encode("utf-8"))
    specs = make_specs(root, manifest, report, operation_id, report_sha, input_sha)
    staging_ref = REF_PREFIX + operation_id
    work = operation_directory(root, operation_id)
    work.mkdir(parents=True, exist_ok=True)
    receipt_path = checked_output(work, "receipt.json")
    if receipt_path.exists():
        receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
        if receipt.get("operationId") != operation_id or receipt.get("inputSha256") != input_sha:
            fail("OPERATION_ID_REUSED", "Existing fixture receipt has different content; inspect original UUID")
        if receipt["status"] == "ABORTED":
            fail("OPERATION_ALREADY_ABORTED", "Preserve the abort evidence and use a newly reviewed operation")
    else:
        receipt = {"schemaVersion": 1, "operationId": operation_id,
                   "inputSha256": input_sha, "reviewedReportSha256": report_sha,
                   "stagingRef": staging_ref, "status": "PREPARING",
                   "authorities": [], "productionCutover": "NOT_AUTHORIZED"}
    existing = {entry["name"]: entry for entry in receipt["authorities"]}
    # Back up all original heads BEFORE the first staged ref. These backups
    # contain immutable original history and cannot be substituted by diffs.
    for spec in specs:
        name = spec["name"]
        repo = Path(spec["repo"])
        prior = existing.get(name)
        expected_backup = checked_output(work, f"{spec['authorityKey']}.bundle")
        if prior:
            if (any(prior[k] != spec[k] for k in
                    ("repo", "originalRef", "originalHead", "path", "authorityKey"))
                    or prior["backup"] != str(expected_backup)):
                fail("OPERATION_ID_REUSED", "Original fixture authority differs from the stored receipt")
            backup = expected_backup
            if not backup.exists() or digest(backup.read_bytes()) != prior["backupSha256"]:
                fail("BACKUP_MISSING", "Restore the exact original Git bundle before resuming")
            continue
        backup = expected_backup
        if backup.exists():
            fail("UNJOURNALED_BACKUP", "Inspect existing original bundle before retrying")
        run_git(repo, "bundle", "create", str(backup), spec["originalRef"])
        run_git(repo, "bundle", "verify", str(backup))
        entry = {k: spec[k] for k in ("name", "repo", "originalRef", "originalHead", "path")}
        entry["authorityKey"] = spec["authorityKey"]
        entry.update({"backup": str(backup), "backupSha256": digest(backup.read_bytes()),
                      "preparedHead": None, "diff": None, "diffSha256": None})
        receipt["authorities"].append(entry)
        existing[name] = entry
        atomic_json(receipt_path, receipt)
    try:
        for spec in specs:
            name, repo = spec["name"], Path(spec["repo"])
            prior = existing[name]
            if name == "account":
                for project in specs[:-1]:
                    if ref_tip(Path(project["repo"]), project["originalRef"]) != project["originalHead"]:
                        fail("COMPETING_WRITER_HEAD_CHANGED",
                             f"{project['name']}: original project ref advanced during staging; "
                             "abort partial fixture refs and independently re-review")
                # Publish account evidence last. Its exact links to both inert
                # project refs make partial multi-ledger staging inspectable
                # without claiming a cross-ledger atomic transaction.
                for item in spec["record"]["preparedProjects"]:
                    project_head = existing[item["name"]]["preparedHead"]
                    if not project_head:
                        fail("PROJECT_ADAPTER_INCOMPLETE",
                             "Resume or abort the project staging using the same UUID")
                    item["preparedHead"] = project_head
            staged = ref_tip(repo, staging_ref)
            if staged:
                if prior["preparedHead"] and staged != prior["preparedHead"]:
                    fail("PREPARED_REF_MISMATCH",
                         f"{name}: staged ref no longer matches its receipt; preserve the unknown commit")
                # Detect retries with changed payloads even if the local receipt
                # was lost after an atomic Git CAS.
                payload = run_git(repo, "show", staged + ":" + spec["path"]).stdout
                if payload != pretty(spec["record"]):
                    fail("OPERATION_ID_REUSED",
                         f"{name}: original operation UUID already staged different content")
                if run_git(repo, "rev-parse", staged + "^").stdout.strip() != spec["originalHead"]:
                    fail("PREPARED_PARENT_MISMATCH", "Inspect and quarantine the unexpected prepared ref")
                # A crash after ref CAS but before receipt persistence is only
                # recoverable by regenerating the exact deterministic commit.
                # Matching IDs and parent alone cannot authorize a sibling tip.
                if staged != stage_commit(root, repo, spec["originalHead"], spec["path"], spec["record"]):
                    fail("PREPARED_REF_MISMATCH",
                         f"{name}: staged commit differs from reviewed evidence; preserve it")
            else:
                staged = stage_commit(root, repo, spec["originalHead"], spec["path"], spec["record"])
                cas_create(repo, spec["originalRef"], spec["originalHead"], staging_ref, staged)
            diff = run_git(repo, "diff", "--no-ext-diff",
                           spec["originalHead"], staged, "--").stdout
            diff_path = checked_output(work, f"{spec['authorityKey']}.diff")
            diff_sha = digest(diff.encode("utf-8"))
            if (prior["diff"] not in (None, str(diff_path))
                    or prior["diffSha256"] not in (None, diff_sha)
                    or (diff_path.exists() and diff_path.read_text(encoding="utf-8") != diff)):
                fail("UNEXPECTED_DIFF_EVIDENCE",
                     f"{name}: preserve and reconcile unrecognized diff instead of overwriting it")
            if not diff_path.exists():
                diff_path.write_text(diff, encoding="utf-8")
            prior.update({"preparedHead": staged, "diff": str(diff_path),
                          "diffSha256": diff_sha})
            atomic_json(receipt_path, receipt)
    except PreparationError:
        receipt["status"] = "PARTIAL_ADAPTER_ONLY"
        atomic_json(receipt_path, receipt)
        raise
    receipt["status"] = "ADAPTER_ONLY_NOT_PREPARED"
    receipt["missingBindings"] = list(MISSING_BINDINGS)
    atomic_json(receipt_path, receipt)
    return {"status": receipt["status"], "operationId": operation_id,
            "productionCutover": "NOT_AUTHORIZED", "schedulerEnabled": False,
            "canonicalPrepared": False, "receipt": str(receipt_path),
            "missingBindings": list(MISSING_BINDINGS),
            "authorities": [{k: entry[k] for k in
                             ("name", "originalHead", "preparedHead", "backup", "diff")}
                            for entry in receipt["authorities"]]}


def abort(args: argparse.Namespace) -> dict:
    if not args.apply_disposable:
        fail("DISABLED_BY_DEFAULT", "Pass --apply-disposable to abort only fixture refs")
    root = disposable_root(args.disposable_root)
    operation_id = str(uuid.UUID(args.operation_id))
    if operation_id != args.operation_id:
        fail("INVALID_OPERATION_ID", "Use the original canonical lowercase UUID")
    receipt_path = checked_output(operation_directory(root, operation_id), "receipt.json")
    if not receipt_path.is_file():
        fail("RECEIPT_MISSING", "Recover the original durable fixture receipt, not a guessed operation")
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    if receipt.get("operationId") != operation_id:
        fail("RECEIPT_ID_MISMATCH", "Inspect the original operation receipt")
    staging_ref = REF_PREFIX + operation_id
    used: set[Path] = set()
    verified_refs = []
    # Verify every observed authority before removing any ref. Unknown sibling
    # commits must not trigger a partial abort elsewhere.
    for entry in reversed(receipt["authorities"]):
        repo = validate_repo(root, entry["repo"], used)
        current = ref_tip(repo, staging_ref)
        if current is None:
            continue
        expected = entry.get("preparedHead")
        if not expected:
            fail("ABORT_UNJOURNALED_STAGING",
                 f"{entry['name']}: replay prepare with the same reviewed inputs and UUID to "
                 "reconstruct/record the exact staged commit before aborting; do not guess")
        if current != expected:
            fail("ABORT_REF_MISMATCH",
                 f"{entry['name']}: expected {expected}, observed {current}; "
                 "preserve this unknown ref and reconcile the competing writer")
        payload = json.loads(run_git(repo, "show", current + ":" + entry["path"]).stdout)
        if (payload.get("operationId") != operation_id
                or payload.get("inputSha256") != receipt["inputSha256"]
                or run_git(repo, "rev-parse", current + "^").stdout.strip() != entry["originalHead"]):
            fail("ABORT_REF_MISMATCH",
                 f"{entry['name']}: preserve unknown ref and inspect its original lineage")
        verified_refs.append((repo, current))
    for repo, current in verified_refs:
        removed = run_git(repo, "update-ref", "--stdin",
                          input_text=f"start\ndelete {staging_ref} {current}\nprepare\ncommit\n",
                          check=False)
        if removed.returncode:
            fail("ABORT_UNCERTAIN",
                 f"Git CAS rejected deletion of {staging_ref}; inspect changed ref and replay the "
                 "same operation only after reconciliation")
        if ref_tip(repo, staging_ref) is not None:
            fail("ABORT_UNCERTAIN", "Re-read the fixture staging ref; do not retry with a new UUID")
    receipt["status"] = "ABORTED"
    receipt["productionCutover"] = "NOT_AUTHORIZED"
    atomic_json(receipt_path, receipt)
    return {"status": "ABORTED", "operationId": operation_id,
            "originalRefsUnchangedByAdapter": True,
            "backupAndDiffEvidenceRetained": True, "receipt": str(receipt_path)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["prepare", "abort"])
    parser.add_argument("--disposable-root", required=True)
    parser.add_argument("--operation-id", required=True)
    parser.add_argument("--apply-disposable", action="store_true")
    parser.add_argument("--manifest")
    parser.add_argument("--report")
    parser.add_argument("--reviewed-sha256")
    args = parser.parse_args()
    try:
        if args.action == "prepare":
            if not all((args.manifest, args.report, args.reviewed_sha256)):
                fail("REVIEWED_INPUT_MISSING",
                     "Supply the manifest, exact reviewed report and SHA-256 pin")
            result = prepare(args)
        else:
            result = abort(args)
        print(pretty(result), end="")
        return 0
    except (PreparationError, ValueError, KeyError, OSError, json.JSONDecodeError) as exc:
        if isinstance(exc, PreparationError):
            code, action = exc.code, exc.next_action
        else:
            code, action = "INVALID_INPUT", str(exc)
        print(pretty({"status": "NO_GO", "code": code, "nextAction": action,
                      "productionCutover": "NOT_AUTHORIZED"}), file=sys.stderr, end="")
        return 2


if __name__ == "__main__":
    sys.exit(main())
