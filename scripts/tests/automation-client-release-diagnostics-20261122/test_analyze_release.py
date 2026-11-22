#!/usr/bin/env python3
"""Self-checks: mutant TRX, missing receipt and negative activity diagnostic cases.
Parser checks are supplemental; real production tests run unfiltered separately.
"""
import json
import tempfile
import unittest
from pathlib import Path
import analyze_release

XML_HEAD = '<?xml version="1.0" encoding="utf-8"?>'
NS = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'

def trx(outcome="Passed", message=""):
    return (XML_HEAD + '<TestRun xmlns="' + NS + '"><Results>'
            '<UnitTestResult testName="ReviewWorkflowTests.AgentInboxBulkLedgerBenchmarkAndSnapshotCorrectness(0)" '
            'outcome="' + outcome + '"><Output><ErrorInfo><Message>' + message +
            '</Message></ErrorInfo></Output></UnitTestResult></Results>'
            '<ResultSummary><Counters total="1" passed="' +
            ("1" if outcome == "Passed" else "0") + '" failed="' +
            ("0" if outcome == "Passed" else "1") + '"/></ResultSummary></TestRun>')

class FailClosedDiagnosticTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.folder = Path(self.tmp.name)
        (self.folder / "terminal.log").write_text(
            "Jarvis activity persistence: agent-seen read failed (UnauthorizedAccessException).\n")

    def manifest(self, exit_code=0, source=analyze_release.SOURCE, trx_present=True):
        result = self.folder / "client-full.trx"
        if trx_present:
            result.write_text(trx())
        (self.folder / "manifest.json").write_text(json.dumps({
            "source": source, "source_after": source, "sdk": analyze_release.SDK,
            "cmd": ["dotnet", "test", "Client.Tests/Client.Tests.fsproj", "-c", "Release"],
            "test_filter": None, "exit_code": exit_code,
            "trx_paths": [str(result)] if trx_present else []}))

    def test_permission_diagnostic_alone_does_not_fabricate_failure(self):
        self.manifest()
        report = analyze_release.classify(self.folder)
        self.assertEqual(report["verdict"], "PASS")
        self.assertEqual(report["activity_negative_logs"], 1)

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
        self.assertEqual(analyze_release.classify(self.folder)["verdict"], "UNKNOWN")

    def test_wrong_source_cannot_be_green(self):
        self.manifest(source="0" * 40)
        self.assertEqual(analyze_release.classify(self.folder)["verdict"], "UNKNOWN")

    def test_no_terminal_exit_cannot_be_green(self):
        (self.folder / "manifest.json").write_text(json.dumps({
            "source": analyze_release.SOURCE, "source_after": analyze_release.SOURCE,
            "sdk": analyze_release.SDK,
            "cmd": ["dotnet", "test", "Client.Tests/Client.Tests.fsproj"]}))
        self.assertEqual(analyze_release.classify(self.folder)["verdict"], "UNKNOWN")

    def test_timeout_exit_zero_without_trx_stays_unknown(self):
        self.manifest(exit_code=0, trx_present=False)
        path = self.folder / "manifest.json"
        payload = json.loads(path.read_text())
        payload["timed_out"] = True
        path.write_text(json.dumps(payload))
        result = analyze_release.classify(self.folder)
        self.assertEqual(result["verdict"], "UNKNOWN")
        self.assertIn("timeout", result["reason"])
        self.assertEqual(result["activity_negative_logs"], 1)

    def test_timeout_with_synthetic_green_trx_stays_unknown(self):
        self.manifest()
        path = self.folder / "manifest.json"
        payload = json.loads(path.read_text())
        payload["timed_out"] = True
        path.write_text(json.dumps(payload))
        self.assertEqual(analyze_release.classify(self.folder)["verdict"], "UNKNOWN")

    def test_filtered_invocation_cannot_be_green(self):
        self.manifest()
        path = self.folder / "manifest.json"
        payload = json.loads(path.read_text())
        payload["test_filter"] = "FullyQualifiedName~SomeTest"
        path.write_text(json.dumps(payload))
        self.assertEqual(analyze_release.classify(self.folder)["verdict"], "UNKNOWN")

    def test_wrong_sdk_cannot_be_green(self):
        self.manifest()
        path = self.folder / "manifest.json"
        payload = json.loads(path.read_text())
        payload["sdk"] = "10.0.100"
        path.write_text(json.dumps(payload))
        self.assertEqual(analyze_release.classify(self.folder)["verdict"], "UNKNOWN")

    def test_zero_exit_with_failed_trx_stays_red(self):
        self.manifest()
        (self.folder / "client-full.trx").write_text(trx("Failed", "unexpected failure"))
        self.assertEqual(analyze_release.classify(self.folder)["verdict"], "RED")

if __name__ == "__main__":
    unittest.main()
