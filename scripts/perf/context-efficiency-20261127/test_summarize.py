#!/usr/bin/env python3
"""Regression tests for the explicit route and activation contracts.

Run with the retained representative and low-threshold artifacts to reproduce
the independently observed false-positive on actual serialized receipts.
"""
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest

import summarize as s


def projection(result="{}", error=None, activation=None):
    return json.dumps({"Result": result, "Error": error,
                       "InboxActivation": activation, "PendingJob": None})


def synthetic(representative=False, ceiling=False, history=220):
    report = {"Representative": representative, "CeilingOnly": ceiling,
              "HistoryTasks": history, "Cycles": [], "Calls": []}
    # Independent ordered fixture definition, not derived from validator.
    targets = ["WorkerReady", "WorkerBusy", "WorkerIdle", "Reviewer"]
    if representative:
        targets += [f"FrontierObserver{i:02}" for i in range(1, 9)]
    groups = [
        ("interpreter", "Interpreter", [("observe-" + t, "ObserveAgent") for t in targets] +
         [("task-frontier-01", "GetTask"), ("task-frontier-02", "GetTask"),
          ("task-frontier-03", "GetTask"), ("review-detail", "ReviewDetails"),
          ("integrations-history", "GetIntegrationSubmissions"),
          ("integration-frontier", "GetIntegrationSubmissions")]),
        ("worker-bootstrap", "WorkerReady", [
            ("worker-inbox", "AgentInbox"), ("worker-instructions", "GetAgentInstructions"),
            ("worker-task", "GetTask"), ("worker-spec-page", "GetTaskSpecPage")]),
        ("reviewer-bootstrap", "Reviewer", [
            ("reviewer-inbox", "ReviewerInbox"), ("reviewer-instructions", "GetAgentInstructions"),
            ("reviewer-task", "GetTask"), ("reviewer-details", "ReviewDetails")]),
        ("overview", "Interpreter", [("task-overview", "TaskOverview")]),
    ]
    if ceiling:
        groups = groups[-1:]
    for cycle, actor, routes in groups:
        report["Cycles"].append({"Name": cycle, "FirstCallIndex": len(report["Calls"]),
                                 "CallCount": len(routes)})
        for name, tool in routes:
            row = {"Scenario": name, "Actor": actor, "Tool": tool, "TerminalClass": "Success",
                   "PendingJobId": None, "PolledJobId": None, "TerminalProjection": projection()}
            if name == "integrations-history" and history == 0:
                error = ('LedgerFailureError\n  { Code = "StaleEntityVersion"\n'
                         '    Message = "Missing ledger entity: tasks/history00000/task.json" }')
                row.update(TerminalClass=error, TerminalProjection=projection(None, error))
            report["Calls"].append(row)
    return report


def remove_route(report, name):
    index = next(i for i, row in enumerate(report["Calls"]) if row["Scenario"] == name)
    del report["Calls"][index]
    for cycle in report["Cycles"]:
        if cycle["FirstCallIndex"] > index:
            cycle["FirstCallIndex"] -= 1
        elif index < cycle["FirstCallIndex"] + cycle["CallCount"]:
            cycle["CallCount"] -= 1


class MatrixTests(unittest.TestCase):
    def test_complete_routes(self):
        for representative, ceiling, history, count in [
            (False, False, 220, 19), (True, False, 526, 27),
            (False, True, 2200, 1), (False, False, 0, 19)
        ]:
            with self.subTest(count=count, history=history):
                self.assertEqual(len(s.terminal_rows(synthetic(representative, ceiling, history))), count)

    def test_omissions_even_if_cycle_counts_adjusted(self):
        for name in ("task-frontier-01", "worker-instructions", "integrations-history"):
            r = synthetic(True)
            remove_route(r, name)
            with self.subTest(name=name), self.assertRaises(ValueError):
                s.terminal_rows(r)

    def test_wrong_actor_tool_order_cycle_and_duplicate(self):
        for field, value in [("Actor", "WorkerIdle"), ("Tool", "GetTask"),
                             ("Scenario", "observe-WorkerBusy")]:
            r = synthetic()
            r["Calls"][0][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                s.terminal_rows(r)
        r = synthetic()
        r["Calls"][0], r["Calls"][1] = r["Calls"][1], r["Calls"][0]
        with self.assertRaises(ValueError):
            s.terminal_rows(r)
        r = synthetic()
        r["Cycles"][0]["CallCount"] -= 1
        r["Cycles"][1]["FirstCallIndex"] -= 1
        r["Cycles"][1]["CallCount"] += 1
        with self.assertRaises(ValueError):
            s.terminal_rows(r)

    def test_unexpected_error_and_ceiling(self):
        r = synthetic()
        r["Calls"][0].update(TerminalClass="Denied", TerminalProjection=projection(None, "Denied"))
        with self.assertRaises(ValueError):
            s.terminal_rows(r)
        r = synthetic(ceiling=True)
        error = 'OutputTruncated\n  "Command response was 2297055 bytes and exceeds the safe response size of 921600 bytes."'
        r["Calls"][0].update(TerminalClass=error, TerminalProjection=projection(None, error))
        self.assertEqual(len(s.terminal_rows(r)), 1)
        r["Calls"][0]["TerminalProjection"] = projection()
        with self.assertRaises(ValueError):
            s.terminal_rows(r)

    def test_exact_handle_poll_and_wrong_handle(self):
        r = synthetic()
        first = r["Calls"][0]
        poll = dict(first, Tool="GetJobResult", PolledJobId="handle")
        first.update(PendingJobId="handle", TerminalClass=None, TerminalProjection=None)
        r["Calls"].insert(1, poll)
        r["Cycles"][0]["CallCount"] += 1
        for c in r["Cycles"][1:]:
            c["FirstCallIndex"] += 1
        self.assertEqual(len(s.terminal_rows(r)), 19)
        poll["PolledJobId"] = "wrong"
        with self.assertRaises(ValueError):
            s.terminal_rows(r)


class ReceiptTests(unittest.TestCase):
    def activation(self, head, operation):
        r = {"Cycles": [{"Name": "worker-bootstrap", "AfterHead": head}]}
        p = {"Result": json.dumps({"SnapshotCommit": head, "stable": "assignment"}),
             "InboxActivation": (
                 '{ ProjectName = "FixtureProject"\n  AgentId = "WorkerReady"\n'
                 '  ActivatedByThisCall = true\n  BeforeState = Some "Ready"\n'
                 '  AfterState = Some "Working"\n  CommittedHead = Some "' + head +
                 '"\n  OperationId = Some "' + operation + '" }')}
        return r, p

    def test_equivalent_and_drift(self):
        r, p = self.activation("a"*40, "1ea8ff51-1c9a-46d5-a6f9-f7971b691959")
        q, t = self.activation("b"*40, "22fcba3a-be5f-470c-b15e-00a46a0d4483")
        self.assertEqual(s.normalize(s.activation_projection(r, p)),
                         s.normalize(s.activation_projection(q, t)))
        t["Result"] = json.dumps({"SnapshotCommit": "b"*40, "stable": "changed"})
        self.assertNotEqual(s.normalize(s.activation_projection(r, p)),
                            s.normalize(s.activation_projection(q, t)))

    def test_malformed_receipt(self):
        r, p = self.activation("a"*40, "1ea8ff51-1c9a-46d5-a6f9-f7971b691959")
        for old, new in [("WorkerReady", "WorkerIdle"), ("true", "false"),
                         ('Some "Working"', 'Some "Ready"'), ("a"*40, "b"*40),
                         ("1ea8ff51", "bad"), ("1ea8ff51-1c9a-46d5-a6f9-f7971b691959",
                                              "00000000-0000-0000-0000-000000000000")]:
            bad = dict(p, InboxActivation=p["InboxActivation"].replace(old, new))
            with self.subTest(old=old), self.assertRaises(ValueError):
                s.activation_projection(r, bad)

    def test_absent_and_mismatched_provenance(self):
        with tempfile.TemporaryDirectory() as directory:
            p = Path(directory) / "report.json"
            p.write_text("{}")
            self.assertEqual(s.provenance(p, {})["status"], "unqualified")
            p.with_name(p.name + ".receipt.json").write_text(json.dumps(
                {"schema": 1, "status": "bound", "artifact_sha256": "incorrect"}))
            self.assertEqual(s.provenance(p, {})["reason"], "Report digest mismatch")


if __name__ == "__main__":
    unittest.main()
