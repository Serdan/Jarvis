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
        if task.get("sourceRefs"):
            require(candidate.get("sourceRef") in task["sourceRefs"],
                    "candidate source ref absent from inspected legacy attempt")
        if task.get("sourceCommits"):
            require(candidate.get("sourceCommit") in task["sourceCommits"],
                    "candidate source commit absent from inspected legacy attempt")

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
