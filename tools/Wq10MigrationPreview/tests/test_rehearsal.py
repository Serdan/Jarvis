"""Hermetic WQ10 two-project rehearsal against pinned disposable Git remotes.

Imports ONLY fixture helpers from accepted test_preview.py. Both project ledgers
and the account ledger have bare remotes and two local clones each. WQ08 JSON is
clearly fixture evidence; it cannot be mistaken for a live authenticated host run.
"""
import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

from test_preview import LedgerFixture, git


ROOT = pathlib.Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "tools/Wq10MigrationPreview/rehearsal.py"
ORIGINAL_ID = "44444444-4444-4444-4444-444444444444"
BROWSER_ID = "55555555-5555-5555-5555-555555555555"


class RehearsalTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="wq10-two-project-")
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.account = LedgerFixture(self.root, "account", "jarvis-account-admission", {
            "state.json": {
                "schemaVersion": 1, "accountId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "ledgerId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                "epoch": 1, "migrationPhase": "Preparing", "stateVersion": 1,
                "configVersion": 1, "nextSequence": 5,
                "maxConcurrentAgents": 1, "tokens": []
            }
        })
        self.projects = {}
        for name, task_id, project_id in (
            ("Alpha", "T-alpha", "cccccccc-cccc-cccc-cccc-cccccccccccc"),
            ("Beta", "T-beta", "dddddddd-dddd-dddd-dddd-dddddddddddd")
        ):
            work_id = f"W-{name}"
            origin_path = "records/work-queue/origins/" + hashlib.sha256(work_id.encode()).hexdigest() + ".json"
            if not hasattr(self, "origin_paths"):
                self.origin_paths = {}
            self.origin_paths[name] = origin_path
            origin = {
                "SchemaVersion": 1, "ProjectId": project_id, "WorkId": work_id,
                "SourceHead": "4" * 40, "SourceHighWater": 9,
                "SourceOperationId": ("66666666-6666-6666-6666-666666666666" if name == "Alpha"
                                      else "77777777-7777-7777-7777-777777777777"),
                "SourceOperationSequence": 8, "SourceOperationKind": "PublishWork",
                "SourceOperationActor": "Worker 1", "SourceRequestHash": "2" * 64,
                "SourceOriginHead": "5" * 40,
                "SourceEntityPath": f"tasks/{task_id}/task.json",
                "SourceEntityBlobOid": "6" * 40, "SourceEntitySha256": "7" * 64,
                "SourceSelector": "Task", "SourceRef": f"refs/heads/feature/{task_id}",
                "SourceCommit": "1" * 40, "RoleId": "Planner", "RoleVersion": 1,
                "AccountCreditStatus": "Available", "Ambiguity": "None"
            }
            self.projects[name] = LedgerFixture(self.root, name.lower(), "jarvis-orchestration", {
                "ledger.json": {
                    "schemaVersion": 3, "projectId": project_id,
                    "ledgerId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
                    "epoch": 1, "nextSequence": 12
                },
                f"tasks/{task_id}/task.json": {
                    "taskId": task_id, "title": f"Legacy task {name}",
                    "lifecycle": "InProgress", "specVersion": 1
                },
                f"tasks/{task_id}/attempts/11111111111111111111111111111111.json": {
                    "taskId": task_id, "attemptId": "11111111111111111111111111111111",
                    "phase": "Finished", "sourceRef": f"refs/heads/feature/{task_id}",
                    "sourceCommit": "1" * 40, "ownerAgentId": "Worker 1"
                },
                "agents/Manager.json": {
                    "agentId": "Manager", "execution": "Stopped"
                },
                "agents/Worker_1.json": {
                    "agentId": "Worker 1", "execution": "Stopped"
                },
                "records/work-queue/project.json": {
                    "schemaVersion": 1, "projectId": project_id,
                    "state": {
                        "migration": {"case": "Prepared", "fields": []},
                        "queueCovered": True, "graphCovered": True,
                        "messageCovered": True, "sourceCovered": True,
                        "works": {f"W-{name}": {
                            "queueId": name,
                            "subjectId": task_id, "stageGeneration": 1,
                            "kind": {"case": "Implementation", "fields": []},
                            "state": {"case": "Available", "fields": []},
                            "sourceLineage": {"repositoryId": f"repo-{name}",
                                              "taskId": task_id, "targetRef": "refs/heads/main",
                                              "lineageId": f"origin-{task_id}"}
                        }},
                        "messages": {
                            f"Task-{name}": {
                                "subject": {"case": "Task", "fields": [task_id]},
                                "state": {"case": "Pending", "fields": []},
                                "workId": f"W-{name}"
                            },
                            f"Role-{name}": {
                                "subject": {"case": "Role", "fields": ["Planner"]},
                                "state": {"case": "Pending", "fields": []},
                                "workId": f"W-{name}"
                            }
                        },
                        "launches": {}
                    }
                },
                origin_path: origin
            })

    def manifest(self, prefix, *, held=False, unknown=False):
        manifest = {
            "schemaVersion": 1,
            "account": {
                "name": "account", "repo": str(self.account.remote),
                "head": self.account.head,
                "ref": "refs/heads/jarvis-account-admission"
            },
            "projects": [
                {"name": name, "repo": str(project.remote),
                 "head": project.head, "ref": "refs/heads/jarvis-orchestration"}
                for name, project in sorted(self.projects.items())
            ],
            "observations": {
                "legacyManagerStopped": not unknown,
                "oldWritersFenced": not unknown,
                "clientProtocolsCompatible": True,
                "pendingOperations": ([{"id": ORIGINAL_ID, "project": "Beta",
                                       "status": "Unknown", "actor": "Worker 1"}]
                                      if unknown else []),
                "inFlightSourceJobs": (["source-job-Beta-9"] if unknown else []),
                "uncertainBrowserSubmissions": ([BROWSER_ID] if unknown else [])
            }
        }
        path = self.root / (prefix + "-manifest.json")
        path.write_text(json.dumps(manifest), encoding="utf-8")
        previews = {}
        for name, project in sorted(self.projects.items()):
            issues = [{"code": "AccountNotActivated", "record": self.account.head,
                       "nextAction": "Requires separate operator activation"}]
            issues.append({"code": "DispatchDisabled", "record": project.head,
                           "nextAction": "Production canDispatch remains OFF. Operator must separately approve cutover."})
            if held:
                issues.append({"code": "CapacityFull", "record": self.account.head,
                               "nextAction": "Verify original terminal token release"})
            if unknown and name == "Beta":
                issues.append({"code": "BrowserJournalUnknown", "record": BROWSER_ID,
                               "nextAction": "Recover original browser effect"})
            preview = {
                "mode": "ReadOnlyDryRun", "canDispatch": False,
                "accountHead": self.account.head, "accountMigration": "Preparing",
                "maxConcurrentAgents": 1, "occupiedAgents": int(held),
                "availableSlots": int(not held),
                "projectHead": project.head, "projectVersion": 1,
                "roleCatalogRevision": 1, "roleId": "Planner",
                "selectedRoleVersion": 1,
                "eligibleWork": [f"W-{name}"], "skippedWork": [],
                "wouldLaunch": {
                    "workId": f"W-{name}", "workGeneration": 1,
                    "queueId": name, "roleId": "Planner", "roleVersion": 1,
                    "sourceHead": "4" * 40, "sourceRef": f"refs/heads/feature/T-{name.lower()}",
                    "sourceCommit": "1" * 40,
                    "sourceOperationId": "66666666-6666-6666-6666-666666666666" if name == "Alpha" else "77777777-7777-7777-7777-777777777777",
                    "sourceRequestHash": "2" * 64,
                    "originBlobOid": git(project.remote, "rev-parse",
                                         f"{project.head}:{self.origin_paths[name]}")
                },
                "existingLaunches": [], "issues": issues
            }
            preview_path = self.root / (prefix + f"-{name}-scheduler.json")
            preview_path.write_text(json.dumps(preview), encoding="utf-8")
            previews[name] = preview_path.name
        return {"name": prefix, "manifest": path.name,
                "schedulerPreviews": previews}

    def run_rehearsal(self, cases, *, markdown=False):
        path = self.root / "rehearsal.json"
        path.write_text(json.dumps({"schemaVersion": 1, "cases": cases}), encoding="utf-8")
        args = [sys.executable, str(SCRIPT), str(path)]
        if markdown:
            args += ["--format", "markdown"]
        result = subprocess.run(args, text=True, capture_output=True,
                                check=False, timeout=90)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout if markdown else json.loads(result.stdout)

    def make_blocked(self):
        state = json.loads((self.account.first / "state.json").read_text())
        state["tokens"] = [{
            "status": "Held", "launchId": "launch-Beta-1",
            "projectId": "dddddddd-dddd-dddd-dddd-dddddddddddd"
        }]
        self.account.commit({"state.json": state}, "reserve disposable global cap one")
        beta = self.projects["Beta"]
        queue = json.loads((beta.first / "records/work-queue/project.json").read_text())
        queue["state"]["launches"]["launch-Beta-1"] = {
            "roleId": "Planner",
            "state": {"case": "ReservedUnregistered", "fields": []},
            "accountTokenId": "launch-Beta-1"
        }
        beta.commit({
            "records/work-queue/project.json": queue,
            "agents/Manager.json": {
                "agentId": "Manager", "execution": "Working",
                "currentAssignment": {"taskId": "T-beta"}
            },
            "tasks/T-beta/attempts/22222222222222222222222222222222.json": {
                "taskId": "T-beta", "attemptId": "22222222222222222222222222222222",
                "phase": "Started", "sourceRef": "refs/heads/feature/T-beta",
                "sourceCommit": "4" * 40, "ownerAgentId": "Manager"
            }
        }, "original Manager still active and launch reserved")
        blocked = self.manifest("blocked", held=True, unknown=True)

        # Simulate a still-capable old writer on the second clone AFTER our exact
        # pinned snapshot. The subsequent ref is observed, never silently adopted.
        git(beta.second, "fetch", "origin")
        git(beta.second, "reset", "--hard", "origin/jarvis-orchestration")
        old_task = beta.second / "tasks/T-beta/task.json"
        data = json.loads(old_task.read_text())
        data["title"] = "Concurrent old client message"
        old_task.write_text(json.dumps(data))
        git(beta.second, "add", ".")
        git(beta.second, "-c", "user.name=OldWriter",
            "-c", "user.email=old@example.test",
            "commit", "-m", "competing old client post-cutover read")
        git(beta.second, "push", "origin", "HEAD:refs/heads/jarvis-orchestration")
        return blocked

    def freeze_prepared(self, case):
        # Retain the green case on independent offline bare remotes. A later
        # adversarial writer must not retroactively make its pinned ref stale.
        manifest_path = self.root / case["manifest"]
        manifest = json.loads(manifest_path.read_text())
        for index, entry in enumerate([manifest["account"], *manifest["projects"]]):
            remote = self.root / f"prepared-snapshot-{index}.git"
            subprocess.run(["git", "clone", "--bare", entry["repo"], str(remote)],
                           check=True, capture_output=True)
            entry["repo"] = str(remote)
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    def test_green_prepared_case_then_no_go_blocked_case_and_read_only(self):
        prepared = self.manifest("prepared")
        self.freeze_prepared(prepared)
        blocked = self.make_blocked()
        heads_before = [self.account.head] + [p.head for p in self.projects.values()]
        response = self.run_rehearsal([prepared, blocked])
        green, red = response["cases"]
        self.assertEqual(green["rehearsalVerdict"], "PREPARED_FOR_REVIEW")
        self.assertEqual(green["productionCutover"], "NOT_AUTHORIZED")
        self.assertEqual(len(green["projectMatrix"]), 2)
        self.assertTrue(all(p["queueMigration"] == "Prepared" for p in green["projectMatrix"]))
        self.assertTrue(any("Task-" in str(p["pendingMessageIds"]) for p in green["projectMatrix"]))
        self.assertEqual(red["rehearsalVerdict"], "NO_GO")
        self.assertEqual(red["account"]["cap"], 1)
        self.assertEqual(red["account"]["held"], 1)
        self.assertIn("launch-Beta-1", red["account"]["heldLaunchIds"])
        self.assertIn(ORIGINAL_ID, str(red["originalUncertainIds"]))
        self.assertIn(BROWSER_ID, str(red["originalUncertainIds"]))
        self.assertEqual(red["writerFences"]["oldWritersFenced"], False)
        self.assertIn("Manager", next(p for p in red["projectMatrix"] if p["project"] == "Beta")["workingAgents"])
        codes = {f["code"] for f in red["blockers"]}
        self.assertTrue({"CAPACITY_OCCUPIED", "REF_HEAD_CHANGED", "LEGACY_AGENT_WORKING",
                         "QUEUED_LAUNCH_ACTIVE", "BROWSER_OUTCOME_UNKNOWN",
                         "OPERATION_UNCERTAIN", "SCHEDULER_PREVIEW_ISSUE"} <= codes)
        self.assertEqual(heads_before,
                         [self.account.head] + [p.head for p in self.projects.values()])
        markdown = self.run_rehearsal([prepared, blocked], markdown=True)
        self.assertIn("Go / No-Go matrix", markdown)
        self.assertIn(self.account.head, markdown)
        self.assertIn("nonforce compare-and-swap", markdown)

    def test_rejects_scheduler_dispatch_claim_and_capacity_mismatch(self):
        prepared = self.manifest("prepared")
        preview_path = self.root / prepared["schedulerPreviews"]["Alpha"]
        data = json.loads(preview_path.read_text())
        data["canDispatch"] = True
        data["occupiedAgents"] = 1
        preview_path.write_text(json.dumps(data), encoding="utf-8")
        report = self.run_rehearsal([prepared])["cases"][0]
        self.assertEqual(report["rehearsalVerdict"], "NO_GO")
        self.assertTrue({"SCHEDULER_NOT_INERT", "CAPACITY_EVIDENCE_CONFLICT"} <=
                        {f["code"] for f in report["blockers"]})

    def test_rejects_unverified_original_and_missing_preview_issue_inventory(self):
        prepared = self.manifest("prepared")
        preview_path = self.root / prepared["schedulerPreviews"]["Beta"]
        data = json.loads(preview_path.read_text())
        data["wouldLaunch"]["sourceOperationId"] = None
        del data["issues"]
        preview_path.write_text(json.dumps(data), encoding="utf-8")
        report = self.run_rehearsal([prepared])["cases"][0]
        self.assertEqual(report["rehearsalVerdict"], "NO_GO")
        self.assertTrue({"SOURCE_PROVENANCE_UNVERIFIED", "SCHEDULER_ISSUES_MISSING"} <=
                        {f["code"] for f in report["blockers"]})

    def test_real_wq08_pascal_case_serialization_is_accepted(self):
        prepared = self.manifest("prepared")

        def as_pascal(value):
            if isinstance(value, dict):
                return {key[:1].upper() + key[1:]: as_pascal(item)
                        for key, item in value.items()}
            if isinstance(value, list):
                return [as_pascal(item) for item in value]
            return value

        for path in prepared["schedulerPreviews"].values():
            snapshot = self.root / path
            snapshot.write_text(json.dumps(as_pascal(json.loads(snapshot.read_text()))),
                                encoding="utf-8")
        report = self.run_rehearsal([prepared])["cases"][0]
        self.assertEqual(report["rehearsalVerdict"], "PREPARED_FOR_REVIEW")
        self.assertTrue(all(p["scheduler"]["mode"] == "ReadOnlyDryRun"
                            for p in report["projectMatrix"]))


    def test_source_backed_host_preview_contract_includes_mandatory_diagnostic(self):
        # The real WQ08 previewWith contract unconditionally emits this code.
        # These are source-backed host-shaped fixtures, NOT live/attested output.
        source = (ROOT / "Client/Modules/WorkQueueSchedulerHostPreview.fs").read_text(encoding="utf-8")
        self.assertIn('add "DispatchDisabled" (projectHead', source)
        self.assertIn('Mode = "ReadOnlyDryRun"', source)
        self.assertIn("CanDispatch = false", source)
        prepared = self.manifest("host-contract")
        for path in prepared["schedulerPreviews"].values():
            preview = json.loads((self.root / path).read_text())
            self.assertEqual(preview["mode"], "ReadOnlyDryRun")
            self.assertIs(preview["canDispatch"], False)
            self.assertEqual([i["code"] for i in preview["issues"]].count("DispatchDisabled"), 1)
        result = self.run_rehearsal([prepared])["cases"][0]
        self.assertEqual(result["rehearsalVerdict"], "PREPARED_FOR_REVIEW")
        self.assertEqual(result["productionCutover"], "NOT_AUTHORIZED")
        for row in result["projectMatrix"]:
            self.assertIn("DispatchDisabled", row["scheduler"]["expectedDiagnostics"])
            self.assertEqual(row["scheduler"]["adverseIssues"], [])

    def test_missing_duplicate_or_cross_project_dispatch_disabled_is_never_green(self):
        for variation in ("missing", "duplicate", "cross-project-record"):
            with self.subTest(variation=variation):
                prepared = self.manifest("dispatch-" + variation)
                path = self.root / prepared["schedulerPreviews"]["Alpha"]
                preview = json.loads(path.read_text())
                issue = next(i for i in preview["issues"] if i["code"] == "DispatchDisabled")
                if variation == "missing":
                    preview["issues"].remove(issue)
                elif variation == "duplicate":
                    preview["issues"].append(dict(issue))
                else:
                    issue["record"] = "9" * 40
                path.write_text(json.dumps(preview))
                result = self.run_rehearsal([prepared])["cases"][0]
                self.assertEqual(result["rehearsalVerdict"], "NO_GO")
                self.assertEqual(result["productionCutover"], "NOT_AUTHORIZED")
                codes = {f["code"] for f in result["blockers"]}
                self.assertTrue({"SCHEDULER_DISPATCH_DIAGNOSTIC_MISSING",
                                 "SCHEDULER_PREVIEW_ISSUE"} & codes)

    def test_adverse_wq08_issues_remain_blocking_despite_inert_dispatch(self):
        prepared = self.manifest("mixed-issues")
        path = self.root / prepared["schedulerPreviews"]["Beta"]
        preview = json.loads(path.read_text())
        for code in ("AccountUnknown", "ProjectHeadConflict", "CapacityFull",
                     "BrowserJournalUnknown"):
            preview["issues"].append({"code": code, "record": "unresolved",
                                      "nextAction": "Inspect original witness"})
        path.write_text(json.dumps(preview))
        result = self.run_rehearsal([prepared])["cases"][0]
        self.assertEqual(result["rehearsalVerdict"], "NO_GO")
        self.assertEqual(result["productionCutover"], "NOT_AUTHORIZED")
        beta = next(r for r in result["projectMatrix"] if r["project"] == "Beta")
        self.assertEqual(set(beta["scheduler"]["adverseIssues"]),
                         {"AccountUnknown", "ProjectHeadConflict", "CapacityFull",
                          "BrowserJournalUnknown"})

    def test_reviewer_exact_two_project_require_candidate_false_bypass(self):
        prepared = self.manifest("reviewer-bypass")
        prepared["requireCandidate"] = False  # untrusted caller-controlled input
        for name, path in prepared["schedulerPreviews"].items():
            preview_path = self.root / path
            data = json.loads(preview_path.read_text())
            data["wouldLaunch"] = None
            preview_path.write_text(json.dumps(data), encoding="utf-8")
        result = self.run_rehearsal([prepared])["cases"][0]
        self.assertEqual(result["rehearsalVerdict"], "NO_GO")
        self.assertEqual(result["productionCutover"], "NOT_AUTHORIZED")
        codes = [f["code"] for f in result["blockers"]]
        self.assertIn("CANDIDATE_BYPASS_REJECTED", codes)
        self.assertEqual(codes.count("SOURCE_PROVENANCE_UNVERIFIED"), 2)
        for row in result["projectMatrix"]:
            self.assertIsNone(row["scheduler"]["wouldLaunch"])
            self.assertEqual(len(row["legacyTasks"]), 1)
            self.assertEqual(len(row["pendingMessageIds"]), 2)

    def test_no_candidate_cannot_be_prepared_even_without_bypass_flag(self):
        prepared = self.manifest("no-candidate")
        for path in prepared["schedulerPreviews"].values():
            preview_path = self.root / path
            data = json.loads(preview_path.read_text())
            data["wouldLaunch"] = None
            preview_path.write_text(json.dumps(data))
        result = self.run_rehearsal([prepared])["cases"][0]
        self.assertEqual(result["rehearsalVerdict"], "NO_GO")
        self.assertEqual([f["code"] for f in result["blockers"]].count(
            "SOURCE_PROVENANCE_UNVERIFIED"), 2)

    def test_candidate_identity_and_source_lineage_must_match_git_inventory(self):
        alterations = (
            ("workId", "W-unrelated"), ("workGeneration", 2), ("queueId", "wrong-queue"),
            ("sourceOperationId", "ffffffff-ffff-ffff-ffff-ffffffffffff"),
            ("sourceRequestHash", "a" * 64), ("sourceHead", "9" * 40),
            ("sourceRef", "refs/heads/other"), ("sourceCommit", "8" * 40),
            ("originBlobOid", "a" * 40), ("roleVersion", 2),
        )
        for key, wrong in alterations:
            with self.subTest(field=key):
                prepared = self.manifest("alter-" + key)
                preview_path = self.root / prepared["schedulerPreviews"]["Alpha"]
                preview = json.loads(preview_path.read_text())
                preview["wouldLaunch"][key] = wrong
                preview_path.write_text(json.dumps(preview))
                result = self.run_rehearsal([prepared])["cases"][0]
                self.assertEqual(result["rehearsalVerdict"], "NO_GO")
                self.assertIn("SOURCE_PROVENANCE_UNVERIFIED",
                              {f["code"] for f in result["blockers"]})

    def test_origin_blob_matches_but_original_identity_disagrees(self):
        beta = self.projects["Beta"]
        origin_path = beta.first / self.origin_paths["Beta"]
        origin = json.loads(origin_path.read_text())
        origin["SourceOperationId"] = "abababab-abab-abab-abab-abababababab"
        beta.commit({self.origin_paths["Beta"]: origin}, "alter source origin at later pinned head")
        prepared = self.manifest("wrong-original")
        result = self.run_rehearsal([prepared])["cases"][0]
        self.assertEqual(result["rehearsalVerdict"], "NO_GO")
        # The expected blob OID is correct, but the actual source identity isn't.
        self.assertIn("candidate sourceOperationId differs",
                      str(result["blockers"]))


if __name__ == "__main__":
    unittest.main()
