import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from verify import verify_manifest

def digest(data):
    return hashlib.sha256(data).hexdigest()


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        def git(*args):
            return subprocess.check_output(["git", "-C", str(self.repo), *args],
                                           stderr=subprocess.DEVNULL, text=True).strip()
        git("init", "--quiet")
        (self.repo / "source").write_text("fixture")
        git("add", "source")
        git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "-qm", "fixture")
        self.commit = git("rev-parse", "HEAD")
        self.tree = git("rev-parse", "HEAD^{tree}")
        self.artifact = self.root / "JarvisClient-linux-x64"
        self.artifact.write_bytes(b"fixture executable bytes")
        self.sha = digest(self.artifact.read_bytes())
        self.target = {"product": "JarvisClient", "rid": "linux-x64",
                       "configuration": "Release"}
        self.source = {"commit": self.commit, "tree": self.tree}
        self.entry = {"id": "client", "path": self.artifact.name,
                      "expectedSha256": self.sha, "observedSha256": self.sha,
                      "target": self.target}
        self.manifest = {"schema": 1, "source": self.source,
                         "artifacts": [self.entry]}
        self.stamp = {"schema": 1, "kind": "source-stamp", "artifactSha256": self.sha,
                      "commit": self.commit, "method": "dotnet-assembly-informational-version"}
        self.build = {"schema": 1, "kind": "build", "buildId": "fixture-build",
                      "source": self.source, "target": self.target, "sdkVersion": "11.0.100",
                      "state": "exited", "exitCode": 0, "artifactSha256": self.sha,
                      "checkoutBefore": {"phase": "before-build", "source": self.source,
                                         "status": "clean", "method": "git-status-porcelain-v1-untracked-all"},
                      "checkoutAfter": {"phase": "after-build", "source": self.source,
                                        "status": "clean", "method": "git-status-porcelain-v1-untracked-all"}}
        self.receipt("stamp", self.stamp)
        self.receipt("build", self.build)
        self.release = {"schema": 1, "kind": "release", "buildId": "fixture-build",
                        "buildReceiptSha256": self.entry["build"]["sha256"],
                        "source": self.source, "target": self.target,
                        "artifactSha256": self.sha, "state": "exited", "exitCode": 0}
        self.receipt("release", self.release)

    def receipt(self, key, value, pin=True):
        data = json.dumps(value).encode()
        path = self.root / (key + ".json")
        path.write_bytes(data)
        self.entry[key] = {"path": path.name}
        if pin:
            self.entry[key]["sha256"] = digest(data)

    def report(self):
        path = self.root / "manifest.json"
        path.write_text(json.dumps(self.manifest))
        return verify_manifest(path, self.repo)

    def artifact_report(self):
        return self.report()["artifacts"][0]

    def test_exact_matching_fixture(self):
        report = self.report()
        self.assertEqual("verified", report["status"])
        self.assertEqual("verified", report["source"]["status"])
        a = report["artifacts"][0]
        self.assertEqual(self.sha, a["actualSha256"])
        for dimension in ("artifactBytes", "observedBytes", "sourceStamp", "buildProvenance"):
            self.assertEqual("verified", a[dimension]["status"], dimension)
        self.assertEqual("fixture-build", a["buildId"])
        self.assertEqual("caller-pinned-receipts", report["trustBoundary"])

    def test_stale_artifact_even_with_new_expected_digest(self):
        self.artifact.write_bytes(b"another build")
        self.entry["expectedSha256"] = digest(self.artifact.read_bytes())
        self.entry["observedSha256"] = self.entry["expectedSha256"]
        a = self.artifact_report()
        self.assertEqual("verified", a["artifactBytes"]["status"])
        self.assertEqual("mismatch", a["sourceStamp"]["status"])
        self.assertEqual("mismatch", a["buildProvenance"]["status"])

    def test_dirty_source(self):
        self.build["checkoutBefore"]["status"] = "dirty"
        self.receipt("build", self.build)
        self.release["buildReceiptSha256"] = self.entry["build"]["sha256"]
        self.receipt("release", self.release)
        self.assertEqual("mismatch", self.artifact_report()["buildProvenance"]["status"])

    def test_checkout_only_after_build_does_not_prove_clean_build(self):
        del self.build["checkoutBefore"]
        self.receipt("build", self.build)
        self.release["buildReceiptSha256"] = self.entry["build"]["sha256"]
        self.receipt("release", self.release)
        self.assertEqual("unknown", self.artifact_report()["buildProvenance"]["status"])

    def test_mismatched_digest_and_stamp(self):
        self.entry["expectedSha256"] = "0" * 64
        self.entry["observedSha256"] = "1" * 64
        self.stamp["commit"] = "2" * 40
        self.receipt("stamp", self.stamp)
        a = self.artifact_report()
        for dimension in ("artifactBytes", "observedBytes", "sourceStamp"):
            self.assertEqual("mismatch", a[dimension]["status"])

    def test_missing_receipts(self):
        for key in ("stamp", "build", "release"):
            del self.entry[key]
        a = self.artifact_report()
        self.assertEqual("verified", a["artifactBytes"]["status"])
        self.assertEqual("unknown", a["sourceStamp"]["status"])
        self.assertEqual("unknown", a["buildProvenance"]["status"])

    def test_unpinned_receipt_is_not_proof(self):
        self.receipt("build", self.build, pin=False)
        self.assertEqual("unknown", self.artifact_report()["buildProvenance"]["status"])

    def test_release_cross_binding(self):
        self.release["buildReceiptSha256"] = "0" * 64
        self.receipt("release", self.release)
        self.assertEqual("mismatch", self.artifact_report()["buildProvenance"]["status"])

    def test_tampered_receipt(self):
        (self.root / "build.json").write_text("{}")
        self.assertEqual("mismatch", self.artifact_report()["buildProvenance"]["status"])

    def test_unknown_terminal_does_not_become_success(self):
        self.build["state"] = "unknown"
        self.build["exitCode"] = None
        self.receipt("build", self.build)
        self.release["buildReceiptSha256"] = self.entry["build"]["sha256"]
        self.receipt("release", self.release)
        self.assertEqual("unknown", self.artifact_report()["buildProvenance"]["status"])

    def test_wrong_git_tree(self):
        self.manifest["source"] = {"commit": self.commit, "tree": "0" * 40}
        self.assertEqual("mismatch", self.report()["source"]["status"])

    def test_self_stamp_and_clean_current_checkout_are_not_build_evidence(self):
        del self.entry["build"]
        del self.entry["release"]
        a = self.artifact_report()
        self.assertEqual("verified", a["sourceStamp"]["status"])
        self.assertEqual("unknown", a["buildProvenance"]["status"])

    def test_git_objects_not_current_checkout(self):
        (self.repo / "source").write_text("dirty now")
        self.assertEqual("verified", self.report()["status"])
        path = self.root / "manifest.json"
        report = verify_manifest(path)
        self.assertEqual("unknown", report["source"]["status"])
        self.assertEqual("unknown", report["artifacts"][0]["buildProvenance"]["status"])

    def test_pinned_malformed_receipt_fails_closed(self):
        data = b'{"schema":1,"kind":"build","kind":"release"}'
        (self.root / "build.json").write_bytes(data)
        self.entry["build"]["sha256"] = digest(data)
        self.release["buildReceiptSha256"] = digest(data)
        self.receipt("release", self.release)
        self.assertEqual("unknown", self.artifact_report()["buildProvenance"]["status"])

    def test_missing_observed_digest_is_unknown(self):
        del self.entry["observedSha256"]
        a = self.artifact_report()
        self.assertEqual("verified", a["artifactBytes"]["status"])
        self.assertEqual("unknown", a["observedBytes"]["status"])
        self.assertEqual("unknown", a["status"])

    def test_missing_artifact_is_not_verified_by_receipts(self):
        self.artifact.unlink()
        a = self.artifact_report()
        self.assertIsNone(a["actualSha256"])
        self.assertEqual("unknown", a["sourceStamp"]["status"])
        self.assertEqual("unknown", a["buildProvenance"]["status"])

    def test_artifact_symlink_does_not_substitute_bytes(self):
        destination = self.artifact.with_suffix(".bin")
        self.artifact.rename(destination)
        try:
            self.artifact.symlink_to(destination.name)
        except (OSError, NotImplementedError):
            self.skipTest("symlinks unavailable")
        a = self.artifact_report()
        self.assertIsNone(a["actualSha256"])
        self.assertEqual("unknown", a["status"])

    def test_oversized_manifest_fails_closed(self):
        path = self.root / "manifest.json"
        path.write_bytes(b" " * (64 * 1024 + 1))
        proc = subprocess.run([sys.executable, "-B", str(Path(__file__).with_name("verify.py")),
                               str(path)], capture_output=True, text=True)
        self.assertEqual(2, proc.returncode)
        self.assertEqual("invalid", json.loads(proc.stdout)["status"])

    def test_malformed_manifest_cli(self):
        path = self.root / "manifest.json"
        path.write_text('{"schema":1,"schema":1}')
        proc = subprocess.run([sys.executable, "-B", str(Path(__file__).with_name("verify.py")),
                               str(path), "--repo", str(self.repo)], capture_output=True, text=True)
        self.assertEqual(2, proc.returncode)
        self.assertEqual("invalid", json.loads(proc.stdout)["status"])

    def cli_report(self, expected_exit):
        path = self.root / "manifest.json"
        path.write_text(json.dumps(self.manifest), encoding="utf-8")
        proc = subprocess.run([sys.executable, "-B", str(Path(__file__).with_name("verify.py")),
                               str(path), "--repo", str(self.repo)], capture_output=True, text=True)
        self.assertEqual(expected_exit, proc.returncode, proc.stderr)
        self.assertEqual("", proc.stderr)
        return json.loads(proc.stdout)

    def test_surrogate_paths_cli(self):
        for field in ("path", "stamp", "build", "release"):
            for surrogate in ("\ud800", "\udfff", "\udc80"):
                with self.subTest(field=field, surrogate=repr(surrogate)):
                    original = copy.deepcopy(self.entry)
                    try:
                        malformed = "bad" + surrogate + ".bin"
                        if field == "path":
                            self.entry["path"] = malformed
                        else:
                            self.entry[field]["path"] = malformed
                        reason = ("invalid_artifact_path" if field == "path"
                                  else "invalid_receipt_reference")
                        self.assertEqual({"status": "invalid", "reasons": [reason]},
                                         self.cli_report(2))
                    finally:
                        self.entry.clear()
                        self.entry.update(original)

    def test_unicode_paths_cli(self):
        path = self.root / "\u00e9-\U0001f600.bin"
        self.artifact.rename(path)
        self.entry["path"] = path.name
        for field in ("stamp", "build", "release"):
            old = self.root / self.entry[field]["path"]
            new = self.root / (field + "-\u00e9-\U0001f600.json")
            old.rename(new)
            self.entry[field]["path"] = new.name
        self.assertEqual("verified", self.cli_report(0)["status"])

    def test_missing_unicode_artifact_cli(self):
        self.entry["path"] = "missing-\u00e9.bin"
        report = self.cli_report(2)
        self.assertEqual("unknown", report["status"])
        self.assertEqual("unknown", report["artifacts"][0]["artifactBytes"]["status"])

    def test_unicode_artifact_digest_mismatch_cli(self):
        path = self.root / "\u00e9.bin"
        self.artifact.rename(path)
        self.entry["path"] = path.name
        self.entry["expectedSha256"] = "0" * 64
        report = self.cli_report(1)
        self.assertEqual("mismatch", report["status"])
        self.assertEqual("mismatch", report["artifacts"][0]["artifactBytes"]["status"])

    def test_multiple_artifacts_not_cross_attributed(self):
        another = copy.deepcopy(self.entry)
        another["id"] = "server"
        another["target"]["product"] = "JarvisServer"
        self.manifest["artifacts"].append(another)
        report = self.report()
        self.assertEqual("verified", report["artifacts"][0]["buildProvenance"]["status"])
        self.assertEqual("mismatch", report["artifacts"][1]["buildProvenance"]["status"])


if __name__ == "__main__":
    unittest.main()
