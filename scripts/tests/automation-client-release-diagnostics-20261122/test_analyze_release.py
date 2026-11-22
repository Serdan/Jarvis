#!/usr/bin/env python3
"""Parser-only source-bound negative controls, not a full Client.Release verdict."""
import copy
import json
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

import analyze_release

NS = analyze_release.TRX_NS
QN = lambda name: f"{{{NS}}}{name}"
PROJECT = "Client.Tests/Client.Tests.fsproj"
RELEASE_CMD = ["dotnet", "test", PROJECT, "-c", "Release"]


def trx(outcome="Passed", message="", summary_outcome=None):
    run = ET.Element(QN("TestRun"))
    results = ET.SubElement(run, QN("Results"))
    result = ET.SubElement(results, QN("UnitTestResult"), {
        "testName": "ReviewWorkflowTests.AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness(0)",
        "outcome": outcome,
    })
    if message:
        output = ET.SubElement(result, QN("Output"))
        error = ET.SubElement(output, QN("ErrorInfo"))
        ET.SubElement(error, QN("Message")).text = message
    summary = ET.SubElement(run, QN("ResultSummary"), {
        "outcome": summary_outcome or ("Failed" if outcome == "Failed" else "Completed"),
    })
    passed = int(outcome == "Passed")
    failed = int(outcome == "Failed")
    ET.SubElement(summary, QN("Counters"), {
        "total": "1", "executed": str(passed + failed),
        "passed": str(passed), "failed": str(failed),
        # The real VSTest/NUnit adapter can leave this aggregate at zero
        # even when individual result rows have NotExecuted outcomes.
        "notExecuted": "0",
    })
    return ET.tostring(run, encoding="unicode")


class FailClosedDiagnosticTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.folder = Path(self.tmp.name)
        (self.folder / "terminal.log").write_text(
            "Jarvis activity persistence: agent-seen read failed (UnauthorizedAccessException).\n")

    def manifest(self, exit_code=0, source=analyze_release.SOURCE,
                 trx_present=True, cmd=None):
        result = self.folder / "client-full.trx"
        if trx_present:
            result.write_text(trx())
        (self.folder / "manifest.json").write_text(json.dumps({
            "source": source, "source_after": source, "sdk": analyze_release.SDK,
            "cmd": copy.copy(RELEASE_CMD) if cmd is None else cmd,
            "test_filter": None, "exit_code": exit_code,
            "trx_paths": [str(result)] if trx_present else [],
        }))

    def rewrite_manifest(self, **changes):
        path = self.folder / "manifest.json"
        payload = json.loads(path.read_text())
        payload.update(changes)
        path.write_text(json.dumps(payload))

    def mutate_trx(self, change):
        path = self.folder / "client-full.trx"
        root = ET.fromstring(path.read_text())
        change(root)
        path.write_text(ET.tostring(root, encoding="unicode"))

    def assert_unknown(self, message_fragment=""):
        report = analyze_release.classify(self.folder)
        self.assertEqual(report["verdict"], "UNKNOWN", report)
        if message_fragment:
            self.assertIn(message_fragment, report["reason"])
        return report

    def test_permission_diagnostic_alone_does_not_fabricate_failure(self):
        self.manifest()
        report = analyze_release.classify(self.folder)
        self.assertEqual(report["verdict"], "PASS")
        self.assertEqual(report["activity_negative_logs"], 1)

    def test_valid_real_adapter_style_skipped_outcome_with_zero_notexecuted(self):
        self.manifest()
        def change(root):
            results = root.find(QN("Results"))
            ET.SubElement(results, QN("UnitTestResult"), {
                "testName": "IntentionallySkipped", "outcome": "NotExecuted"})
            counters = root.find(f"{QN('ResultSummary')}/{QN('Counters')}")
            counters.set("total", "2")
            counters.set("executed", "1")
            counters.set("notExecuted", "0")
        self.mutate_trx(change)
        report = analyze_release.classify(self.folder)
        self.assertEqual(report["verdict"], "PASS")
        self.assertEqual(report["passed"], 1)
        self.assertEqual(report["skipped"], 1)

    def test_valid_notexecuted_aggregate_matches_skip_count(self):
        self.manifest()
        def change(root):
            ET.SubElement(root.find(QN("Results")), QN("UnitTestResult"), {
                "testName": "Skipped", "outcome": "NotExecuted"})
            counters = root.find(f"{QN('ResultSummary')}/{QN('Counters')}")
            counters.set("total", "2")
            counters.set("notExecuted", "1")
        self.mutate_trx(change)
        self.assertEqual(analyze_release.classify(self.folder)["verdict"], "PASS")

    def test_13_over_12_git_regression_is_real_red(self):
        self.manifest(exit_code=1)
        (self.folder / "client-full.trx").write_text(
            trx("Failed", "Expected: less than or equal to 12 But was: 13"))
        report = analyze_release.classify(self.folder)
        self.assertEqual(report["verdict"], "RED")
        self.assertEqual(report["failed_count"], 1)
        self.assertEqual(len(report["git_process_contract_failures"]), 1)

    def test_no_trx_cannot_be_green(self):
        self.manifest(trx_present=False)
        self.assert_unknown("TRX")

    def test_wrong_source_cannot_be_green(self):
        self.manifest(source="0" * 40)
        self.assert_unknown("source")

    def test_no_terminal_exit_cannot_be_green(self):
        self.manifest()
        path = self.folder / "manifest.json"
        value = json.loads(path.read_text())
        del value["exit_code"]
        path.write_text(json.dumps(value))
        self.assert_unknown("terminal exit")

    def test_timeout_exit_zero_without_trx_stays_unknown(self):
        self.manifest(exit_code=0, trx_present=False)
        self.rewrite_manifest(timed_out=True)
        report = self.assert_unknown("timeout")
        self.assertEqual(report["activity_negative_logs"], 1)

    def test_timeout_with_synthetic_green_trx_stays_unknown(self):
        self.manifest()
        self.rewrite_manifest(timed_out=True)
        self.assert_unknown("timeout")

    def test_filtered_invocation_cannot_be_green(self):
        self.manifest()
        self.rewrite_manifest(test_filter="FullyQualifiedName~SomeTest")
        self.assert_unknown("unfiltered")

    def test_wrong_sdk_cannot_be_green(self):
        self.manifest()
        self.rewrite_manifest(sdk="10.0.100")
        self.assert_unknown("SDK")

    def test_zero_exit_with_failed_trx_stays_red(self):
        self.manifest()
        (self.folder / "client-full.trx").write_text(trx("Failed", "unexpected failure"))
        self.assertEqual(analyze_release.classify(self.folder)["verdict"], "RED")

    def test_missing_resultsummary_cannot_pass(self):
        self.manifest()
        self.mutate_trx(lambda root: root.remove(root.find(QN("ResultSummary"))))
        self.assert_unknown("ResultSummary")

    def test_missing_counters_cannot_pass(self):
        self.manifest()
        def change(root):
            summary = root.find(QN("ResultSummary"))
            summary.remove(summary.find(QN("Counters")))
        self.mutate_trx(change)
        self.assert_unknown("Counters")

    def test_absent_or_negative_summary_outcome_cannot_pass(self):
        for outcome in (None, "Aborted", "InProgress", "Failed"):
            with self.subTest(outcome=outcome):
                self.manifest()
                def change(root):
                    summary = root.find(QN("ResultSummary"))
                    if outcome is None:
                        summary.attrib.pop("outcome", None)
                    else:
                        summary.set("outcome", outcome)
                self.mutate_trx(change)
                self.assert_unknown()

    def test_duplicate_summary_cannot_pass(self):
        self.manifest()
        def change(root):
            other = copy.deepcopy(root.find(QN("ResultSummary")))
            root.append(other)
        self.mutate_trx(change)
        self.assert_unknown("ResultSummary")

    def test_summary_counter_mutants_rejected(self):
        for field, bad in (("total", "0"), ("total", "2"),
                           ("executed", "0"), ("executed", "2"),
                           ("passed", "0"), ("passed", "2"),
                           ("failed", "1"), ("notExecuted", "1"),
                           ("skipped", "7"), ("error", "1"),
                           ("total", "-1"), ("passed", "abc")):
            with self.subTest(field=field, bad=bad):
                self.manifest()
                def change(root):
                    root.find(f"{QN('ResultSummary')}/{QN('Counters')}").set(field, bad)
                self.mutate_trx(change)
                self.assert_unknown("TRX")

    def test_missing_required_counter_rejected(self):
        self.manifest()
        def change(root):
            root.find(f"{QN('ResultSummary')}/{QN('Counters')}").attrib.pop("executed")
        self.mutate_trx(change)
        self.assert_unknown("counter")

    def test_malformed_xml_rejected(self):
        self.manifest()
        (self.folder / "client-full.trx").write_text("<TestRun><broken")
        self.assert_unknown("TRX")

    def test_zero_summary_with_one_positive_outcome_rejected(self):
        self.manifest()
        def change(root):
            counters = root.find(f"{QN('ResultSummary')}/{QN('Counters')}")
            for key in ("total", "executed", "passed", "failed", "notExecuted"):
                counters.set(key, "0")
        self.mutate_trx(change)
        self.assert_unknown("TRX")

    def test_release_invocation_variants_positive(self):
        for option in (["-c", "Release"], ["--configuration", "Release"],
                       ["--configuration=Release"]):
            with self.subTest(option=option):
                self.manifest(cmd=["dotnet", "test", PROJECT, *option])
                self.assertEqual(analyze_release.classify(self.folder)["verdict"], "PASS")

    def test_debug_missing_conflicting_and_malformed_release_rejected(self):
        mutations = [
            ["dotnet", "test", PROJECT],  # missing
            ["dotnet", "test", PROJECT, "-c", "Debug"],
            ["dotnet", "test", PROJECT, "--configuration", "Debug"],
            ["dotnet", "test", PROJECT, "--configuration=Debug"],
            ["dotnet", "test", PROJECT, "-c", "Release", "-c", "Debug"],
            ["dotnet", "test", PROJECT, "-c", "Release", "--configuration", "Release"],
            ["dotnet", "test", PROJECT, "-c"],
            ["dotnet", "test", PROJECT, "--configuration"],
            ["dotnet", "test", PROJECT, "--configuration="],
            ["dotnet", "test", PROJECT, "-c=Release"],
            ["dotnet", "test", PROJECT, "--config", "Release"],
            ["dotnet", "test", PROJECT, "-c", "Release", "-p:Configuration=Debug"],
            ["dotnet", "test", PROJECT, "-c", "Release", "/p:Configuration=Debug"],
            ["dotnet", "test", PROJECT, "-c", "Release", "--property", "Configuration=Debug"],
            ["dotnet", "test", PROJECT, "-c", "Release", "/property:Configuration=Debug"],
            ["dotnet", "test", PROJECT, "-c", "Release", "--Configuration=Debug"],
            ["dotnet", "test", PROJECT, "-c", "Release", "--filter", "Name~Foo"],
            ["dotnet", "test", PROJECT, "--", "-c", "Release"],
        ]
        for cmd in mutations:
            with self.subTest(cmd=cmd):
                self.manifest(cmd=cmd)
                self.assert_unknown("Release invocation")


if __name__ == "__main__":
    unittest.main()
