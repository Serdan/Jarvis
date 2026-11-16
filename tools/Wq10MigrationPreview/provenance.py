"""Pinned, read-only WQ04 provenance checks for the WQ10 rehearsal.

All inputs are operator-supplied offline snapshots. A successful comparison
proves local consistency, not upstream authentication or cutover authority.
"""
import hashlib
import json
import re
import subprocess


OID = re.compile(r"^[0-9a-f]{40}(?:[0-9a-f]{24})?$")
SHA = re.compile(r"^[0-9a-f]{64}$")


def field(value, key):
    if not isinstance(value, dict):
        return None
    return next((item for name, item in value.items() if name.casefold() == key.casefold()), None)


def oid(value):
    return isinstance(value, str) and OID.fullmatch(value) is not None


def sha(value):
    return isinstance(value, str) and SHA.fullmatch(value) is not None


def git_json_at(snapshot, path):
    """Read one JSON blob and its Git OID from an exact pinned commit."""
    spec = f"{snapshot['head']}:{path}"

    def read(*args):
        result = subprocess.run(["git", "-C", snapshot["repo"], *args],
                                capture_output=True, text=True, timeout=20, check=False)
        if result.returncode:
            raise ValueError(f"{path}: pinned Git blob unavailable: {result.stderr.strip()}")
        return result.stdout.strip()

    blob = read("rev-parse", "--verify", spec)
    if not oid(blob):
        raise ValueError(f"{path}: Git blob identity unavailable")
    try:
        result = json.loads(read("show", spec))
    except json.JSONDecodeError as ex:
        raise ValueError(f"{path}: invalid JSON in pinned Git blob: {ex}") from ex
    if not isinstance(result, dict):
        raise ValueError(f"{path}: expected a pinned JSON object")
    return result, blob


def verify_source_writer(task, snapshot, work_id, lineage, candidate):
    """Reconcile the writer tuple with original attempts, not aggregated refs.

    The inspector's sourceRefs combines sourceRef and targetRef for display.
    That union cannot prove which original attempt supplied a target.
    """
    task_id = task.get("taskId")
    paths = task.get("attempts")
    if not isinstance(paths, list) or not paths:
        return [f"{work_id}: no independently inventoried legacy attempts for {task_id}"]

    attempts = []
    failures = []
    prefix = f"tasks/{task_id}/attempts/"
    for path in paths:
        if (not isinstance(path, str) or not path.startswith(prefix) or
                not re.fullmatch(r"[0-9a-f]{32}\.json", path[len(prefix):])):
            failures.append(f"{work_id}: invalid inventoried attempt path {path!r}")
            continue
        try:
            original, _ = git_json_at(snapshot, path)
        except (ValueError, KeyError, OSError, subprocess.TimeoutExpired) as ex:
            failures.append(f"{work_id}: original attempt {path}: {ex}")
            continue
        attempt_id = field(original, "attemptId")
        if (field(original, "taskId") != task_id or not isinstance(attempt_id, str) or
                attempt_id.replace("-", "").lower() + ".json" != path[len(prefix):]):
            failures.append(f"{work_id}: original attempt identity disagrees with {path}")
            continue
        attempts.append(original)

    target = field(lineage, "targetRef")
    original_targets = sorted({ref for attempt in attempts
                               if isinstance(ref := field(attempt, "targetRef"), str) and ref})
    if target not in original_targets:
        failures.append(f"{work_id}: canonical source-writer targetRef {target!r} differs from "
                        f"original attempt targetRefs {original_targets!r} at pinned {snapshot['head']}")

    # The target, source ref and commit must coexist on ONE original attempt.
    # Flattened independent lists can manufacture a false witness.
    matching = [attempt for attempt in attempts
                if field(attempt, "targetRef") == target and
                field(attempt, "sourceRef") == candidate.get("sourceRef") and
                field(attempt, "sourceCommit") == candidate.get("sourceCommit")]
    if not matching:
        failures.append(f"{work_id}: no original attempt at pinned {snapshot['head']} "
                        f"matches targetRef={target!r}, sourceRef={candidate.get('sourceRef')!r}, "
                        f"sourceCommit={candidate.get('sourceCommit')!r} as one source-writer tuple")

    for attempt in matching:
        attempt_id = field(attempt, "attemptId")
        fetch = field(attempt, "sourceRemote")
        push = field(attempt, "sourcePushRemote")
        repository = field(attempt, "sourceRepositoryId")
        if not all(isinstance(value, str) and value for value in (fetch, push, repository)):
            failures.append(f"{work_id}: original attempt {attempt_id} lacks independently "
                            "verifiable source repository endpoints/identity")
            continue

        def fingerprint(prefix):
            return hashlib.sha256(f"{prefix}\n{fetch}\n{push}".encode("utf-8")).hexdigest()

        expected_legacy = fingerprint("jarvis-source-repository-v1")
        expected_queue = fingerprint("wq03-source-git-v1")
        if repository != expected_legacy or field(lineage, "repositoryId") != expected_queue:
            failures.append(f"{work_id}: source repository identity differs for original "
                            f"attempt {attempt_id}: canonical={field(lineage, 'repositoryId')!r}, "
                            f"expected={expected_queue!r}, legacy={repository!r}, "
                            f"expectedLegacy={expected_legacy!r}")
        original_lineage = field(attempt, "lineageId")
        if original_lineage is not None and original_lineage != field(lineage, "lineageId"):
            failures.append(f"{work_id}: original attempt {attempt_id} lineageId "
                            f"{original_lineage!r} differs from canonical {field(lineage, 'lineageId')!r}")
    return failures


def verify_candidate(project, snapshot, preview):
    """Check WQ08 selected work against WQ10 inventory and immutable WQ04 origin."""
    candidate = preview.get("wouldLaunch")
    if not isinstance(candidate, dict) or not isinstance(candidate.get("workId"), str) or not candidate["workId"]:
        return ["WQ08 selected no work with a verifiable original source receipt"]
    work_id = candidate["workId"]
    failures = []

    def require(condition, reason):
        if not condition:
            failures.append(reason)

    try:
        header, _ = git_json_at(snapshot, "ledger.json")
        queue, _ = git_json_at(snapshot, "records/work-queue/project.json")
        origin_path = ("records/work-queue/origins/" +
                       hashlib.sha256(work_id.encode("utf-8")).hexdigest() + ".json")
        origin, origin_blob = git_json_at(snapshot, origin_path)
    except (ValueError, KeyError, OSError, subprocess.TimeoutExpired) as ex:
        return [str(ex)]

    project_id = field(header, "projectId")
    require(isinstance(project_id, str) and bool(project_id) and
            field(queue, "projectId") == project_id and field(origin, "ProjectId") == project_id,
            "pinned ledger, canonical queue and origin project identities disagree")
    require(field(origin, "WorkId") == work_id, "origin names a different immutable work ID")
    require(oid(candidate.get("originBlobOid")) and
            candidate["originBlobOid"] == origin_blob,
            "candidate origin blob OID differs from the pinned Git blob")

    state = field(queue, "state")
    works = field(state, "works")
    work = works.get(work_id) if isinstance(works, dict) else None
    if not isinstance(work, dict):
        return failures + ["selected work ID absent from inspected canonical work inventory"]

    generation = field(work, "stageGeneration")
    require(type(generation) is int and generation > 0 and
            type(candidate.get("workGeneration")) is int and candidate["workGeneration"] == generation,
            "candidate generation disagrees with inspected work stage")
    require(isinstance(field(work, "queueId"), str) and
            candidate.get("queueId") == field(work, "queueId"),
            "candidate queue ID disagrees with inspected canonical work")
    require(field(field(work, "state"), "case") == "Available",
            "candidate is not Available in the inspected queue")
    eligible = preview.get("eligibleWork")
    skipped = preview.get("skippedWork")
    require(isinstance(eligible, list) and work_id in eligible and
            isinstance(skipped, list) and work_id not in skipped,
            "candidate is missing from WQ08 eligible work or is skipped")

    lineage = field(work, "sourceLineage")
    require(isinstance(lineage, dict) and all(
        isinstance(field(lineage, k), str) and field(lineage, k)
        for k in ("repositoryId", "taskId", "targetRef", "lineageId")),
        "canonical work has no complete source-writer lineage")
    stages = [(task, stage)
              for task in project.get("tasks", [])
              for stage in task.get("existingStages", [])
              if stage.get("workId") == work_id]
    require(len(stages) == 1,
            "candidate not uniquely represented in inspected legacy task-stage inventory")
    if len(stages) == 1:
        task, stage = stages[0]
        subject = field(work, "subjectId")
        task_id = task.get("taskId")
        require(isinstance(task_id, str) and
                subject in (task_id, "task-" + task_id) and
                field(lineage, "taskId") == task_id,
                "candidate subject/source lineage disagrees with inspected legacy task")
        require(stage.get("stageGeneration") == generation and
                stage.get("kind") == field(field(work, "kind"), "case") and
                stage.get("state") == field(field(work, "state"), "case"),
                "inspector stage disagrees with pinned canonical work")
        failures.extend(verify_source_writer(task, snapshot, work_id, lineage, candidate))

    require(candidate.get("roleId") == preview.get("roleId") == field(origin, "RoleId") and
            type(candidate.get("roleVersion")) is int and candidate["roleVersion"] > 0 and
            candidate["roleVersion"] == preview.get("selectedRoleVersion") == field(origin, "RoleVersion"),
            "selected versioned role differs from WQ04 source origin")
    for key, source_key in (
        ("sourceHead", "SourceHead"), ("sourceRef", "SourceRef"),
        ("sourceCommit", "SourceCommit"), ("sourceOperationId", "SourceOperationId"),
        ("sourceRequestHash", "SourceRequestHash")):
        supplied = candidate.get(key)
        require(isinstance(supplied, str) and bool(supplied) and
                supplied == field(origin, source_key),
                f"candidate {key} differs from pinned WQ04 source origin")

    require(oid(field(origin, "SourceHead")) and
            oid(field(origin, "SourceCommit")) and
            oid(field(origin, "SourceOriginHead")) and
            oid(field(origin, "SourceEntityBlobOid")) and
            sha(field(origin, "SourceRequestHash")) and
            sha(field(origin, "SourceEntitySha256")) and
            all(isinstance(field(origin, k), str) and field(origin, k)
                for k in ("SourceEntityPath", "SourceSelector", "SourceOperationActor",
                          "SourceOperationKind", "SourceOperationId", "SourceRef")) and
            type(field(origin, "SourceOperationSequence")) is int and
            field(origin, "SourceOperationSequence") > 0 and
            type(field(origin, "SourceHighWater")) is int and
            field(origin, "SourceHighWater") > 0,
            "origin lacks complete original source evidence/high-water")
    return failures
