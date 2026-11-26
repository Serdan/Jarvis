"""Verify fixture-local identities before normalizing cross-run derived values.

Only disposable bare fixture repositories are read. Unknown hashes are never
masked; exact within-run ref targets remain mandatory. The verifier fails closed
if evidence is missing or a receipt does not match its exact object content.
"""
from __future__ import annotations

import hashlib
import json
import re
import subprocess
from datetime import datetime
from pathlib import Path

HEX_OID = re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z")
LEDGER = "refs/heads/jarvis-orchestration"
MAIN = "refs/heads/main"
REGISTER = "register-agent"
NO_EFFECT = (
    "retry-same-operation",
    "operation-id-reused-with-changed-input",
    "unauthorized-register-agent",
)
TIME_FIELDS = {
    "createdat", "stateenteredatutc", "workingturnstartedatutc",
    "createdatutc", "updatedatutc", "registeredatutc",
}
EXPECTED_WRITE_PATHS = {
    "agents/QmVuY2htYXJrV29ya2Vy.json",
    "ops/40/40000000000040008000000000000001.json",
    "ledger.json",
}
IDENTITY_FIELDS = {
    "head", "headOid", "snapshotCommit", "originatingCommit", "verifiedLedgerHead",
    "committedHead", "contentSha256", "originalResultHash",
}
IDENTITY_FIELDS = {key.lower() for key in IDENTITY_FIELDS}


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def encoded(value: object) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def targets(measurement: dict, suffix: str) -> dict[str, str]:
    rows = measurement.get("RefTargets" + suffix)
    require(isinstance(rows, list) and bool(rows), "missing exact ref target evidence")
    result: dict[str, str] = {}
    for row in rows:
        name, oid = row.split("\t")
        require(name not in result and HEX_OID.fullmatch(oid) is not None, "invalid or duplicate ref target")
        result[name] = oid
    require(sorted(result) == measurement.get("Refs" + suffix), "ref name and target evidence disagree")
    require(set(result) == {MAIN, LEDGER}, "unexpected fixture ref set")
    return result


class FixtureIdentity:
    def __init__(self, report: dict):
        self.root = Path(report["FixtureRoot"]).resolve(strict=True)
        self.remote = Path(report["FixtureRemote"]).resolve(strict=True)
        require(self.remote == self.root / "remote.git", "not the recorded disposable bare fixture")
        require(self.root.name.startswith("jarvis-git-call-optimization-"), "unexpected fixture directory name")
        require(self.git("rev-parse", "--is-bare-repository").strip() == b"true", "fixture is not a bare repository")
        self.aliases: dict[str, str] = {}
        self.scenarios = {m["Scenario"]: m for m in report["Measurements"]}
        write = self.scenarios[REGISTER]
        before, after = targets(write, "Before"), targets(write, "After")
        require(before[MAIN] == after[MAIN] == report["FixturePublishedSha"], "source ref changed unexpectedly")
        require(before[LEDGER] != after[LEDGER], "registration did not advance exact ledger ref")
        require(self.git("rev-parse", after[LEDGER] + "^").decode().strip() == before[LEDGER],
                "registration is not exactly one descendant commit")
        self.aliases[before[LEDGER]] = "<ledger-before-register>"
        self.aliases[after[LEDGER]] = "<ledger-after-register>"
        self.before = before
        self.after = after
        old, new = self.tree(before[LEDGER]), self.tree(after[LEDGER])
        changed = {p for p in set(old) | set(new) if old.get(p) != new.get(p)}
        require(changed == EXPECTED_WRITE_PATHS, f"unexpected registered write paths: {changed}")
        require(set(old) <= set(new), "registration removed fixture state")
        old_header = json.loads(old["ledger.json"])
        new_header = json.loads(new["ledger.json"])
        require(new_header["nextSequence"] == old_header["nextSequence"] + 1,
                "registration sequence did not advance exactly once")
        require({k: v for k, v in old_header.items() if k != "nextSequence"} ==
                {k: v for k, v in new_header.items() if k != "nextSequence"},
                "registration changed unrelated ledger header fields")
        for name in NO_EFFECT:
            measurement = self.scenarios[name]
            require(targets(measurement, "Before") == after == targets(measurement, "After"),
                    f"{name} changed exact canonical refs")
        live = dict(line.split("\t") for line in self.git(
            "for-each-ref", "--format=%(refname)%09%(objectname)", "refs/heads"
        ).decode().splitlines())
        require(live == after, "retained fixture refs do not match the final observed state")
        # Verify both observed snapshots, then build stable aliases for their
        # actually present contents. Receipts refer to exact byte hashes, not
        # simply arbitrary values that look like SHA-256.
        self.verify_tree(old)
        self.verify_tree(new)
        self.old_state = self.project_tree(old)
        self.new_state = self.project_tree(new)
        self.verify_payloads(old, new)

    def git(self, *args: str) -> bytes:
        return subprocess.run(["git", "--git-dir", str(self.remote), *args],
                              check=True, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE).stdout

    def tree(self, oid: str) -> dict[str, bytes]:
        require(HEX_OID.fullmatch(oid) is not None, "invalid snapshot OID")
        entries = self.git("ls-tree", "-rz", oid).split(b"\0")
        result: dict[str, bytes] = {}
        for entry in entries:
            if not entry:
                continue
            meta, raw_path = entry.split(b"\t", 1)
            mode, kind, blob = meta.decode().split()
            path = raw_path.decode()
            require(kind == "blob" and mode == "100644" and path not in result,
                    "unexpected fixture tree entry")
            result[path] = self.git("cat-file", "blob", blob)
        return result

    def verify_tree(self, tree: dict[str, bytes]) -> None:
        for path, data in tree.items():
            if not path.startswith("ops/"):
                # Normalize only known fixture timestamps in actual JSON state.
                value = self.project(json.loads(data)) if path.endswith(".json") else data.decode()
                self.aliases[digest(data)] = "<content:" + digest(encoded(value)) + ">"
        for path, data in tree.items():
            if not path.startswith("ops/"):
                continue
            receipt = json.loads(data)
            raw_result = receipt["originalResultJson"]
            raw_request = receipt["canonicalRequestJson"]
            require(digest(raw_result.encode()) == receipt["originalResultHash"],
                    f"invalid original result hash in {path}")
            require(digest(raw_request.encode()) == receipt["requestHash"],
                    f"invalid request hash in {path}")
            original = json.loads(raw_result)
            for change in original["changes"] + original["entityResults"]:
                changed_path = change["path"]
                require(changed_path in tree and digest(tree[changed_path]) == change["contentSha256"],
                        f"receipt content does not match exact object: {changed_path}")
            self.aliases[receipt["originalResultHash"]] = (
                "<result:" + digest(encoded(self.project(original))) + ">"
            )

    def project(self, value: object, field: str = "", pre_normalized: bool = False) -> object:
        if isinstance(value, dict):
            return {key: self.project(item, key, pre_normalized) for key, item in value.items()}
        if isinstance(value, list):
            return [self.project(item, field, pre_normalized) for item in value]
        if not isinstance(value, str):
            return value
        if field.lower() in TIME_FIELDS:
            # Preserve field existence, nullability and valid timestamp shape.
            if not (pre_normalized and value == "<TIME>"):
                require(datetime.fromisoformat(value.replace("Z", "+00:00")).tzinfo is not None,
                        "fixture timestamp is missing its UTC offset")
            return "<fixture-timestamp>"
        if field.lower() in IDENTITY_FIELDS and value in self.aliases:
            return self.aliases[value]
        # JSON strings are used for MCP content and embedded operation
        # receipts. Preserve their structure; never strip unknown hashes.
        if value.lstrip().startswith(("{", "[")):
            try:
                parsed = json.loads(value)
            except json.JSONDecodeError:
                pass
            else:
                return {"$embeddedJson": self.project(parsed, pre_normalized=pre_normalized)}
        return value.replace(str(self.root), "$FIXTURE")

    def project_tree(self, tree: dict[str, bytes]) -> dict[str, object]:
        return {
            path: self.project(json.loads(data)) if path.endswith(".json") else data.decode()
            for path, data in tree.items()
        }

    def verify_payloads(self, old: dict[str, bytes], new: dict[str, bytes]) -> None:
        def tool(name: str) -> dict:
            return json.loads(self.scenarios[name]["ToolResultProjection"])

        def payload(name: str) -> dict:
            return json.loads(tool(name)["content"][0]["text"])

        seed = self.git("show", self.git("rev-parse", self.before[MAIN] + "^").decode().strip() + ":README.md").decode()
        published = self.git("show", self.before[MAIN] + ":README.md").decode()
        require(seed != published, "fixture publication did not change README")
        for name in self.scenarios:
            if name.startswith("read-file-"):
                expected = published if name.startswith("read-file-after-") else seed
                observed = json.loads(tool(name)["content"][0]["text"])
                require(observed == expected, f"{name} did not return exact expected README bytes")

        for name, key in (("get-task", "snapshotCommit"), ("task-overview", "SnapshotCommit"),
                          ("agent-inbox", "SnapshotCommit")):
            require(payload(name)[key] == self.before[LEDGER], f"{name} did not read the exact pre-write snapshot")
        task = payload("get-task")
        require(task["task"]["TaskId"] == "perf-task-0001" and task["task"]["Version"] == 1,
                "unexpected fixture task payload")
        require(task["specBlobOid"] == self.git("rev-parse", self.before[LEDGER] + ":tasks/perf-task-0001/spec.html").decode().strip(),
                "task specification did not match exact blob")
        require(task["specHtml"] == old["tasks/perf-task-0001/spec.html"].decode(), "task specification bytes differ")
        require(payload("task-overview")["Tasks"] == [{"Attempt": None, "Task": task["task"]}], "task overview differs from pinned task")
        inbox = payload("agent-inbox")
        operator = json.loads(old["agents/T3BlcmF0b3I.json"])
        require(inbox["Agent"]["AgentId"] == "Operator" and inbox["Agent"]["StateEnteredAtUtc"] == operator["stateEnteredAtUtc"],
                "inbox did not observe the exact fixture operator state")
        require(inbox["InstructionLayers"]["SnapshotCommit"] == self.before[LEDGER] and
                tool("agent-inbox")["_meta"]["jarvis/inbox_activation"]["ActivatedByThisCall"] is False,
                "parked fixture inbox unexpectedly activated or used a different instruction snapshot")
        receipt = json.loads(new["ops/40/40000000000040008000000000000001.json"])
        for name in (REGISTER, "retry-same-operation", "get-operation-status"):
            result = payload(name)
            require({key.lower(): value for key, value in result["operation"].items()} ==
                    {key.lower(): value for key, value in receipt.items()}, f"{name} returned a different operation receipt")
            require(result["originalResult"] == json.loads(receipt["originalResultJson"]), f"{name} returned a different original result")
            if name == "get-operation-status":
                require(result["disposition"] == "Applied" and result["originatingCommit"] == self.after[LEDGER] and
                        result["snapshotCommit"] == self.after[LEDGER], "operation status has wrong exact commit/disposition")
            else:
                require(result["head"] == self.after[LEDGER] and result["alreadyApplied"] is (name != REGISTER),
                        f"{name} returned wrong exact head or retry classification")
        require(tool("operation-id-reused-with-changed-input")["isError"] is True and
                "OperationIdReused" in tool("operation-id-reused-with-changed-input")["content"][0]["text"], "missing changed-input denial")
        require(tool("unauthorized-register-agent")["isError"] is True and
                "Missing required OAuth scope: git:write" in tool("unauthorized-register-agent")["content"][0]["text"], "missing OAuth scope denial")

    def projection(self, measurement: dict, name: str) -> object:
        raw = measurement.get(name)
        if raw is None:
            return None
        if name == "ResponseProjection":
            require(digest(raw.encode()) == measurement.get("ResponseDigest"), "response projection digest mismatch")
        return self.project(json.loads(raw), pre_normalized=name == "ResponseProjection")

    def state_projection(self, measurement: dict, suffix: str) -> object:
        if not measurement.get("RefTargets" + suffix):
            require(measurement.get("StateDigest" + suffix) is None,
                    "state digest without exact targets")
            return None
        ref_targets = targets(measurement, suffix)
        require(measurement.get("StateDigest" + suffix) is not None,
                "exact targets without state digest")
        if ref_targets == self.before:
            return self.old_state
        if ref_targets == self.after:
            return self.new_state
        raise ValueError("state is outside the verified fixture transition")
