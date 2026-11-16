"""Disposable-only WQ10 staged-adapter tests: never contacts a real Jarvis authority."""
import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

from test_preview import LedgerFixture, git, APP

ROOT = pathlib.Path(__file__).resolve().parents[3]
PREPARE = ROOT / "tools/Wq10MigrationPreview/prepare.py"
sys.path.insert(0, str(PREPARE.parent))
from prepare import cas_create, PreparationError

OPERATION = "44444444-4444-4444-4444-444444444444"
STAGING_REF = "refs/heads/wq10-prepared/" + OPERATION


def maybe_ref(repo, ref):
    output = subprocess.run(["git", "-C", str(repo), "rev-parse", "--verify",
                             "--quiet", ref], text=True, capture_output=True, check=False)
    return output.stdout.strip() if output.returncode == 0 else None


class PreparedAdapterTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="wq10-prepared-adapter-")
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        (self.root / ".wq10-disposable-fixture").write_text(
            "WQ10_DISPOSABLE_FIXTURE_V1\n", encoding="utf-8")
        self.account = LedgerFixture(self.root, "account", "jarvis-account-admission", {
            "state.json": {
                "schemaVersion": 1, "accountId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "ledgerId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                "epoch": 1, "migrationPhase": "Frozen", "stateVersion": 1,
                "configVersion": 1, "nextSequence": 1,
                "maxConcurrentAgents": 2, "tokens": []
            }
        })
        self.projects = []
        for index, name in enumerate(("Alpha", "Beta")):
            task_id = f"legacy-{name.lower()}"
            source_sha = str(index + 1) * 40
            project = LedgerFixture(self.root, name.lower(), "jarvis-orchestration", {
                "ledger.json": {
                    "schemaVersion": 3,
                    "projectId": ("cccccccc-cccc-cccc-cccc-cccccccccccc" if index == 0
                                  else "dddddddd-dddd-dddd-dddd-dddddddddddd"),
                    "ledgerId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
                    "epoch": 1, "nextSequence": 9
                },
                f"tasks/{task_id}/task.json": {
                    "taskId": task_id, "title": name + " work",
                    "lifecycle": "InProgress", "specVersion": 1
                },
                f"tasks/{task_id}/attempts/11111111111111111111111111111111.json": {
                    "taskId": task_id, "attemptId": "11111111111111111111111111111111",
                    "phase": "Finished", "sourceRef": "refs/heads/feature/" + task_id,
                    "sourceCommit": source_sha, "ownerAgentId": "Worker 2"
                },
                "agents/Manager.json": {
                    "agentId": "Manager", "execution": "Stopped"
                },
                "agents/Worker_2.json": {
                    "agentId": "Worker 2", "execution": "Stopped"
                },
                "notifications/historical.json": {
                    "targetAgentId": "Worker 2", "message": "retain original recipient"
                },
                "operations/original.json": {
                    "operationId": "77777777-7777-7777-7777-777777777777",
                    "actor": "Worker 2"
                }
            })
            self.projects.append((name, project))
        self.manifest = {
            "schemaVersion": 1,
            "account": {
                "name": "account", "repo": str(self.account.remote),
                "head": self.account.head, "ref": "refs/heads/jarvis-account-admission"
            },
            "projects": [
                {"name": name, "repo": str(project.remote), "head": project.head,
                 "ref": "refs/heads/jarvis-orchestration"}
                for name, project in self.projects
            ],
            "observations": {
                "legacyManagerStopped": True,
                "oldWritersFenced": True,
                "clientProtocolsCompatible": True,
                "pendingOperations": [], "inFlightSourceJobs": [],
                "uncertainBrowserSubmissions": []
            }
        }
        self.manifest_path = self.root / "manifest.json"
        self.report_path = self.root / "reviewed-preflight.json"
        self.refresh_report()
        self.originals = {
            "account": self.account.head,
            **{name: project.head for name, project in self.projects}
        }

    def refresh_report(self):
        self.manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        proc = subprocess.run(["dotnet", str(APP), str(self.manifest_path)],
                              text=True, capture_output=True, timeout=30)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.report_path.write_text(proc.stdout, encoding="utf-8")
        self.report = json.loads(proc.stdout)

    def command(self, action="prepare", enabled=True):
        args = [sys.executable, str(PREPARE), action,
                "--disposable-root", str(self.root), "--operation-id", OPERATION]
        if enabled:
            args += ["--apply-disposable"]
        if action == "prepare":
            args += ["--manifest", str(self.manifest_path), "--report",
                     str(self.report_path), "--reviewed-sha256",
                     hashlib.sha256(self.report_path.read_bytes()).hexdigest()]
        result = subprocess.run(args, text=True, capture_output=True, timeout=100)
        return result, json.loads(result.stdout if result.returncode == 0 else result.stderr)

    def all_authorities(self):
        return [("account", self.account.remote, "refs/heads/jarvis-account-admission"),
                *[(name, project.remote, "refs/heads/jarvis-orchestration")
                  for name, project in self.projects]]

    def test_default_off_two_projects_account_idempotent_reversible(self):
        self.assertEqual(self.report["readiness"], "PREVIEW_READY_FOR_REVIEW")
        disabled, failure = self.command(enabled=False)
        self.assertNotEqual(disabled.returncode, 0)
        self.assertEqual(failure["code"], "DISABLED_BY_DEFAULT")
        for _, repo, _ in self.all_authorities():
            self.assertIsNone(maybe_ref(repo, STAGING_REF))
        result, prepared = self.command()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(prepared["status"], "ADAPTER_ONLY_NOT_PREPARED")
        self.assertFalse(prepared["canonicalPrepared"])
        self.assertFalse(prepared["schedulerEnabled"])
        self.assertIn("WQ07_ACCOUNT_MIGRATION_PREPARE_CAS_AND_RECEIPT",
                      prepared["missingBindings"])
        first_heads = {}
        for name, repo, ref in self.all_authorities():
            self.assertEqual(git(repo, "rev-parse", ref), self.originals[name])
            stage = maybe_ref(repo, STAGING_REF)
            self.assertIsNotNone(stage)
            first_heads[name] = stage
            self.assertEqual(git(repo, "rev-parse", stage + "^"), self.originals[name])
            changes = git(repo, "diff", "--name-only", self.originals[name], stage).splitlines()
            self.assertEqual(len(changes), 1)
            self.assertIn("prepared-adapter/" + OPERATION + ".json", changes[0])
            payload = json.loads(git(repo, "show", stage + ":" + changes[0]))
            self.assertFalse(payload["migrationPrepared"])
            self.assertFalse(payload["schedulerEnabled"])
            self.assertEqual(payload["productionCutover"], "NOT_AUTHORIZED")
            if name != "account":
                self.assertTrue(payload["legacyHistoryImmutable"])
                self.assertEqual(payload["newLaunches"], [])
                self.assertEqual(payload["newClaims"], [])
                self.assertEqual(payload["proposedStages"][0]["proposedState"], "Blocked")
                self.assertFalse(payload["proposedStages"][0]["runnable"])
                self.assertEqual(payload["proposedStages"][0]["roleId"], None)
                self.assertEqual(payload["proposedStages"][0]["dependencies"], None)
                self.assertEqual(payload["legacyAgents"][0]["agentId"], "Manager")
            self.assertIn("tasks/" if name != "account" else "state.json",
                          git(repo, "ls-tree", "-r", "--name-only", stage))
        account_plan = json.loads(git(
            self.account.remote, "show", first_heads["account"] + ":records/"
            "wq10-prepared-adapter/" + OPERATION + ".json"))
        self.assertEqual(
            [(entry["name"], entry["preparedHead"]) for entry in account_plan["preparedProjects"]],
            [(name, first_heads[name]) for name, _ in self.projects])
        self.assertTrue(all(not entry["canonicalPrepared"]
                            for entry in account_plan["preparedProjects"]))
        receipt = json.loads(pathlib.Path(prepared["receipt"]).read_text())
        self.assertEqual(receipt["status"], "ADAPTER_ONLY_NOT_PREPARED")
        for item in receipt["authorities"]:
            self.assertTrue(pathlib.Path(item["backup"]).is_file())
            self.assertTrue(pathlib.Path(item["diff"]).is_file())
            self.assertEqual(hashlib.sha256(pathlib.Path(item["backup"]).read_bytes()).hexdigest(),
                             item["backupSha256"])
            git(pathlib.Path(item["repo"]), "bundle", "verify", item["backup"])
        again, replay = self.command()
        self.assertEqual(again.returncode, 0, again.stderr)
        self.assertEqual(replay["status"], "ADAPTER_ONLY_NOT_PREPARED")
        for name, repo, _ in self.all_authorities():
            self.assertEqual(maybe_ref(repo, STAGING_REF), first_heads[name])
        aborted, answer = self.command(action="abort")
        self.assertEqual(aborted.returncode, 0, aborted.stderr)
        self.assertEqual(answer["status"], "ABORTED")
        for name, repo, ref in self.all_authorities():
            self.assertIsNone(maybe_ref(repo, STAGING_REF))
            self.assertEqual(git(repo, "rev-parse", ref), self.originals[name])
        again_abort, replay_abort = self.command(action="abort")
        self.assertEqual(again_abort.returncode, 0, again_abort.stderr)
        self.assertEqual(replay_abort["status"], "ABORTED")
        reused, failure = self.command()
        self.assertNotEqual(reused.returncode, 0)
        self.assertEqual(failure["code"], "OPERATION_ALREADY_ABORTED")

    def test_reused_id_different_reviewed_input_rejected(self):
        applied, _ = self.command()
        self.assertEqual(applied.returncode, 0, applied.stderr)
        self.manifest["account"]["name"] = "account-renamed"
        self.refresh_report()
        self.assertEqual(self.report["readiness"], "PREVIEW_READY_FOR_REVIEW")
        result, failure = self.command()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(failure["code"], "OPERATION_ID_REUSED")

    def test_competing_original_writer_preserves_original_history(self):
        _, beta = self.projects[1]
        beta.commit({"notifications/new.json": {"note": "competing old writer"}},
                    "other writer advances original ref")
        result, failure = self.command()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(failure["code"], ("PREFLIGHT_REPORT_CHANGED", "PREFLIGHT_NOT_READY"))
        for _, repo, _ in self.all_authorities():
            self.assertIsNone(maybe_ref(repo, STAGING_REF))
        self.assertNotEqual(beta.head, self.originals["Beta"])

    def test_missing_lineage_blocks_before_any_stage(self):
        _, alpha = self.projects[0]
        task_id = "legacy-alpha"
        alpha.commit({
            f"tasks/{task_id}/attempts/11111111111111111111111111111111.json": {
                "taskId": task_id, "attemptId": "11111111111111111111111111111111",
                "phase": "Finished", "ownerAgentId": "Worker 2"
            }
        }, "erase lineage in fixture")
        self.manifest["projects"][0]["head"] = alpha.head
        self.refresh_report()
        self.assertEqual(self.report["readiness"], "PREVIEW_READY_FOR_REVIEW")
        result, failure = self.command()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(failure["code"], "SOURCE_LINEAGE_MISSING")
        for _, repo, _ in self.all_authorities():
            self.assertIsNone(maybe_ref(repo, STAGING_REF))

    def test_unknown_staged_content_never_overwritten(self):
        applied, _ = self.command()
        self.assertEqual(applied.returncode, 0, applied.stderr)
        _, beta = self.projects[1]
        # Another UUID's private staging ref is a conflict, not permission to
        # silently replace or update its prepared data.
        git(beta.remote, "update-ref", "refs/heads/wq10-prepared/" +
            "55555555-5555-5555-5555-555555555555", beta.head)
        result, failure = self.command()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(failure["code"], "OTHER_PREPARATION_PRESENT")
        self.assertEqual(git(beta.remote, "rev-parse", "refs/heads/jarvis-orchestration"),
                         self.originals["Beta"])


    def test_cas_rejects_head_race_at_write_boundary(self):
        _, alpha = self.projects[0]
        old = alpha.head
        alpha.commit({"notifications/race.json": {"message": "competing writer"}},
                     "race immediately before Git ref CAS")
        with self.assertRaises(PreparationError) as observed:
            cas_create(alpha.remote, "refs/heads/jarvis-orchestration", old,
                       STAGING_REF, old)
        self.assertEqual(observed.exception.code, "COMPETING_WRITER_OR_PREPARATION")
        self.assertIsNone(maybe_ref(alpha.remote, STAGING_REF))
        self.assertEqual(git(alpha.remote, "rev-parse", "refs/heads/jarvis-orchestration"),
                         alpha.head)

    def test_nonbare_fixture_authority_refused(self):
        name, project = self.projects[0]
        self.manifest["projects"][0]["repo"] = str(project.first)
        self.refresh_report()
        self.assertEqual(self.report["readiness"], "PREVIEW_READY_FOR_REVIEW")
        result, failure = self.command()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(failure["code"], "NOT_BARE_FIXTURE")
        for _, repo, _ in self.all_authorities():
            self.assertIsNone(maybe_ref(repo, STAGING_REF))

    def test_tampered_preflight_report_rejected(self):
        payload = json.loads(self.report_path.read_text())
        payload["summary"] = "Edited after preflight"
        self.report_path.write_text(json.dumps(payload), encoding="utf-8")
        result, failure = self.command()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(failure["code"], "PREFLIGHT_REPORT_CHANGED")
        for _, repo, _ in self.all_authorities():
            self.assertIsNone(maybe_ref(repo, STAGING_REF))


if __name__ == "__main__":
    unittest.main()
