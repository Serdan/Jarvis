"""Disposable two-clone acceptance tests for the WQ-10 offline migration preview.

Never reads a real Jarvis ledger. Both remotes and every clone live in TemporaryDirectory.
Run after dotnet build -c Release:
  python3 -m unittest discover -s tools/Wq10MigrationPreview/tests -v
"""
import json
import pathlib
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
APP = ROOT / "tools/Wq10MigrationPreview/bin/Release/net11.0/Wq10MigrationPreview.dll"


def git(where, *args):
    result = subprocess.run(["git", "-C", str(where), *args], text=True,
                            capture_output=True, check=False, timeout=20)
    if result.returncode:
        raise AssertionError("git " + " ".join(args) + ": " + result.stderr)
    return result.stdout.strip()


class LedgerFixture:
    def __init__(self, parent, name, branch, records):
        self.remote = parent / f"{name}.git"
        self.first = parent / f"{name}-first"
        self.second = parent / f"{name}-second"
        subprocess.run(["git", "init", "--bare", f"--initial-branch={branch}",
                        str(self.remote)], check=True, capture_output=True)
        subprocess.run(["git", "init", f"--initial-branch={branch}",
                        str(self.first)], check=True, capture_output=True)
        git(self.first, "remote", "add", "origin", str(self.remote))
        self.branch = branch
        self.commit(records, "initial", first=True)
        git(self.first, "push", "-u", "origin", f"HEAD:refs/heads/{branch}")
        subprocess.run(["git", "clone", str(self.remote), str(self.second)],
                       check=True, capture_output=True)

    @property
    def head(self):
        return git(self.remote, "rev-parse", f"refs/heads/{self.branch}")

    def commit(self, records, message="update", first=False):
        for relative, data in records.items():
            path = self.first / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(data, indent=2), encoding="utf-8")
        git(self.first, "add", ".")
        git(self.first, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test",
            "commit", "-m", message)
        if not first:
            git(self.first, "push", "origin", f"HEAD:refs/heads/{self.branch}")


class PreviewTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="jarvis-wq10-")
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.account = LedgerFixture(self.root, "account", "jarvis-account-admission", {
            "state.json": {
                "schemaVersion": 1, "accountId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "ledgerId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                "epoch": 1, "migrationPhase": "Frozen", "stateVersion": 1,
                "configVersion": 1, "nextSequence": 1,
                "maxConcurrentAgents": 2, "tokens": []
            },
        })
        self.task = {
            "taskId": "T1", "title": "A task under migration",
            "lifecycle": "InProgress", "specVersion": 1
        }
        self.project = LedgerFixture(self.root, "project", "jarvis-orchestration", {
            "ledger.json": {
                "schemaVersion": 3, "projectId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
                "ledgerId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
                "epoch": 1, "nextSequence": 8
            },
            "tasks/T1/task.json": self.task,
            "tasks/T1/attempts/11111111111111111111111111111111.json": {
                "taskId": "T1", "attemptId": "11111111111111111111111111111111",
                "phase": "Finished", "sourceRef": "refs/heads/feature/T1",
                "sourceCommit": "1" * 40, "ownerAgentId": "Worker 9"
            },
            "agents/Worker_9.json": {"agentId": "Worker 9", "execution": "Stopped"},
            "notifications/22222222222222222222222222222222.json": {
                "targetAgentId": "Worker 9", "message": "Historical alert"
            },
            "operations/33333333333333333333333333333333.json": {
                "operationId": "33333333-3333-3333-3333-333333333333",
                "actor": "Worker 9", "sequence": 7
            }
        })
        self.manifest = {
            "schemaVersion": 1,
            "account": {"name": "account", "repo": str(self.account.remote),
                        "head": self.account.head,
                        "ref": "refs/heads/jarvis-account-admission"},
            "projects": [{"name": "Demo", "repo": str(self.project.remote),
                          "head": self.project.head,
                          "ref": "refs/heads/jarvis-orchestration"}],
            "observations": {
                "legacyManagerStopped": True,
                "oldWritersFenced": True,
                "clientProtocolsCompatible": True,
                "pendingOperations": [],
                "inFlightSourceJobs": [],
                "uncertainBrowserSubmissions": []
            }
        }

    def preview(self, markdown=False):
        manifest_path = self.root / "manifest.json"
        manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        command = ["dotnet", str(APP), str(manifest_path)]
        result = subprocess.run(command, text=True, capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        if markdown:
            markdown_result = subprocess.run(command + ["--format", "markdown"],
                                             text=True, capture_output=True, timeout=30)
            self.assertEqual(markdown_result.returncode, 0, markdown_result.stderr)
            (self.root / "report.md").write_text(markdown_result.stdout, encoding="utf-8")
        return json.loads(result.stdout)

    def with_queue(self, works=None, messages=None):
        queue = {
            "schemaVersion": 1, "projectId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
            "state": {
                "migration": {"case": "Frozen", "fields": []},
                "works": works or {}, "messages": messages or {}, "launches": {}
            }
        }
        self.project.commit({"records/work-queue/project.json": queue}, "queue fixture")
        self.manifest["projects"][0]["head"] = self.project.head

    def test_ordinary_legacy_preview_retains_source_and_evidence(self):
        before_account, before_project = self.account.head, self.project.head
        report = self.preview(markdown=True)
        self.assertEqual(report["readiness"], "PREVIEW_READY_FOR_REVIEW")
        self.assertEqual(report["activation"], "NOT_AUTHORIZED")
        self.assertEqual(report["projects"][0]["tasks"][0]["disposition"], "PROPOSE")
        self.assertEqual(report["projects"][0]["tasks"][0]["sourceRefs"],
                         ["refs/heads/feature/T1"])
        self.assertEqual(report["projects"][0]["historicalNotifications"], 1)
        self.assertEqual(report["projects"][0]["historicalOperations"], 1)
        self.assertEqual(before_account, self.account.head)
        self.assertEqual(before_project, self.project.head)
        self.assertIn("Authorized manual repair path", (self.root / "report.md").read_text())

    def test_already_migrated_skips_duplicate_and_preserves_messages(self):
        self.with_queue(
            works={"W1": {"subjectId": "T1", "state": {"case": "Blocked"}}},
            messages={
                "M1": {"subject": {"case": "Role", "fields": ["Lead"]},
                       "state": {"case": "Pending", "fields": []},
                       "workId": "W2"},
                "M2": {"subject": {"case": "Task", "fields": ["T1"]},
                       "state": {"case": "Pending", "fields": []}}
            })
        report = self.preview()
        self.assertEqual(report["projects"][0]["tasks"][0]["disposition"], "ALREADY_PRESENT")
        self.assertEqual(len(report["projects"][0]["messages"]), 2)
        self.assertIn("MESSAGES_PENDING", [f["code"] for f in report["findings"]])

    def test_incomplete_and_uncertain_evidence_is_reported_not_dropped(self):
        self.manifest["observations"].update({
            "oldWritersFenced": False,
            "pendingOperations": [{
                "id": "00000000-0000-0000-0000-000000000001",
                "project": "Demo", "status": "Unknown", "actor": "Worker 4"
            }],
            "inFlightSourceJobs": ["job-1"],
            "uncertainBrowserSubmissions": ["session-1"],
        })
        report = self.preview()
        self.assertEqual(report["readiness"], "NO_GO")
        self.assertEqual(report["pendingOperations"][0]["actor"], "Worker 4")
        self.assertEqual(report["pendingOperations"][0]["status"], "Unknown")
        self.assertTrue({"OPERATION_UNCERTAIN", "OLD_WRITERS_UNFENCED",
                         "SOURCE_JOB_IN_FLIGHT", "BROWSER_OUTCOME_UNKNOWN"}
                        <= {f["code"] for f in report["findings"]})

    def test_competing_clone_old_client_write_detected_at_pinned_head(self):
        pinned = self.project.head
        git(self.project.second, "checkout", self.project.branch)
        path = self.project.second / "tasks/T1/task.json"
        path.write_text(json.dumps({**self.task, "title": "Concurrent old client edit"}))
        git(self.project.second, "add", ".")
        git(self.project.second, "-c", "user.name=Old Client",
            "-c", "user.email=old@example.test", "commit", "-m", "old-client-change")
        git(self.project.second, "push", "origin",
            "HEAD:refs/heads/jarvis-orchestration")
        self.assertNotEqual(pinned, self.project.head)
        report = self.preview()
        self.assertEqual(report["readiness"], "NO_GO")
        self.assertEqual(report["projects"][0]["head"], pinned)
        self.assertEqual(report["projects"][0]["currentRefTip"], self.project.head)
        self.assertIn("REF_HEAD_CHANGED", [f["code"] for f in report["findings"]])

    def test_multiple_existing_canonical_stages_halt_conversion(self):
        self.with_queue(works={
            "W1": {"subjectId": "T1"}, "W2": {"subjectId": "T1"}
        })
        report = self.preview()
        self.assertEqual(report["readiness"], "NO_GO")
        self.assertIn("MULTIPLE_CANONICAL_STAGES",
                      [f["code"] for f in report["findings"]])

    def test_claimed_review_unsettled_integration_and_working_agent(self):
        self.project.commit({
            "agents/Manager.json": {
                "agentId": "Manager", "execution": "Working",
                "currentAssignment": {"taskId": "T1"}
            },
            "tasks/T1/reviews/44444444444444444444444444444444/review.json": {
                "reviewId": "R1", "taskId": "T1", "status": "Claimed",
                "reviewerId": "Reviewer 1", "reviewedHeadOid": "a" * 40
            },
            "tasks/T1/integrations/55555555555555555555555555555555.json": {
                "submissionId": "I1", "taskId": "T1", "status": "Pending",
                "submittedCommit": "b" * 40, "reviewId": "R1"
            }
        }, "unsettled historical records")
        self.manifest["projects"][0]["head"] = self.project.head
        report = self.preview()
        self.assertEqual(report["readiness"], "NO_GO")
        self.assertTrue({"CLAIMED_REVIEW", "INTEGRATION_UNSETTLED", "LEGACY_AGENT_WORKING"}
                        <= {f["code"] for f in report["findings"]})
        self.assertIn("Manager", [a["agentId"] for a in report["projects"][0]["agents"]] )
        self.assertEqual(report["projects"][0]["reviews"][0]["reviewedHeadOid"], "a" * 40)
        self.assertEqual(report["projects"][0]["integrations"][0]["status"], "Pending")

    def test_held_account_capacity_and_live_queue_launch_are_visible(self):
        self.account.commit({
            "state.json": {
                "schemaVersion": 1, "accountId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "ledgerId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                "epoch": 1, "migrationPhase": "Frozen", "stateVersion": 2,
                "configVersion": 1, "nextSequence": 2, "maxConcurrentAgents": 2,
                "tokens": [{"status": "Held", "launchId": "L1", "projectId": "Demo"}]
            }
        }, "held token")
        self.manifest["account"]["head"] = self.account.head
        # Synthetic canonical queue with a reserved unregistered launch.
        self.project.commit({
            "records/work-queue/project.json": {
                "schemaVersion": 1, "state": {"migration": {"case": "Frozen", "fields": []},
                    "works": {}, "messages": {},
                    "launches": {"L1": {"roleId": "Worker",
                                       "state": {"case": "ReservedUnregistered"},
                                       "accountTokenId": "L1"}}}
            }
        }, "reserved launch")
        self.manifest["projects"][0]["head"] = self.project.head
        report = self.preview()
        self.assertEqual(report["readiness"], "NO_GO")
        self.assertEqual(report["account"]["heldLaunchIds"], ["L1"])
        self.assertEqual(report["projects"][0]["launches"][0]["state"], "ReservedUnregistered")
        self.assertTrue({"CAPACITY_OCCUPIED", "QUEUED_LAUNCH_ACTIVE"}
                        <= {f["code"] for f in report["findings"]})

    def test_report_is_deterministic_and_read_only(self):
        first = self.preview()
        second = self.preview()
        self.assertEqual(first, second)
        self.assertEqual(git(self.account.first, "status", "--porcelain"), "")
        self.assertEqual(git(self.project.first, "status", "--porcelain"), "")
        self.assertEqual(first["mode"], "OFFLINE_READ_ONLY")

    def test_missing_protocol_and_manager_proof_not_assumed_safe(self):
        self.manifest["observations"] = {}
        report = self.preview()
        self.assertEqual(report["readiness"], "NO_GO")
        self.assertTrue({"MANAGER_NOT_SETTLED", "OLD_WRITERS_UNFENCED", "PROTOCOL_UNVERIFIED"}
                        <= {f["code"] for f in report["findings"]})


if __name__ == "__main__":
    unittest.main()
