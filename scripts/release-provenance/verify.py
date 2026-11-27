#!/usr/bin/env python3
"""Offline verification of explicitly supplied, digest-pinned release evidence."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys

MAX_JSON = 64 * 1024
MAX_ARTIFACTS = 16
HEX = re.compile(r"[0-9a-f]+")
TOKEN = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}")


class InvalidEvidence(ValueError):
    pass


def require(condition, reason):
    if not condition:
        raise InvalidEvidence(reason)


def oid(value, lengths):
    return isinstance(value, str) and len(value) in lengths and HEX.fullmatch(value) is not None


def token(value):
    return isinstance(value, str) and TOKEN.fullmatch(value) is not None


def source_identity(value):
    return (isinstance(value, dict) and set(value) == {"commit", "tree"}
            and oid(value["commit"], (40, 64))
            and oid(value["tree"], (len(value["commit"]),)))


def target_identity(value):
    return (isinstance(value, dict) and set(value) == {"product", "rid", "configuration"}
            and value["product"] in ("JarvisClient", "JarvisServer")
            and token(value["rid"]) and value["configuration"] == "Release")


def explicit_path(value):
    return isinstance(value, str) and 0 < len(value) <= 4096 and "\x00" not in value


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate_json_key")
        result[key] = value
    return result


def invalid_constant(_):
    raise InvalidEvidence("nonfinite_json_number")


def parse_json(raw):
    try:
        return json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object,
                          parse_constant=invalid_constant)
    except (UnicodeError, ValueError, RecursionError) as exc:
        raise InvalidEvidence("invalid_json") from exc


def file_observation(path, *, json_input=False):
    """Hash the same bytes we inspect; do not follow a final-component symlink
    on platforms with O_NOFOLLOW, block on FIFOs, or accept a changing file."""
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NONBLOCK", 0)
    flags |= getattr(os, "O_NOFOLLOW", 0)
    initial = path.lstat()
    if stat.S_ISLNK(initial.st_mode):
        raise InvalidEvidence("symlink_not_supported")
    require(stat.S_ISREG(initial.st_mode), "not_regular_file")
    fd = os.open(path, flags)
    with os.fdopen(fd, "rb") as stream:
        before = os.fstat(stream.fileno())
        require(stat.S_ISREG(before.st_mode), "not_regular_file")
        require(os.path.samestat(initial, before), "file_replaced_before_read")
        h = hashlib.sha256()
        if json_input:
            raw = stream.read(MAX_JSON + 1)
            require(len(raw) <= MAX_JSON, "json_size_limit")
            h.update(raw)
        else:
            raw = None
            count = 0
            while chunk := stream.read(1024 * 1024):
                h.update(chunk)
                count += len(chunk)
            require(count == before.st_size, "file_changed_during_read")
        after = os.fstat(stream.fileno())
        require((before.st_size, before.st_mtime_ns, before.st_ctime_ns)
                == (after.st_size, after.st_mtime_ns, after.st_ctime_ns),
                "file_changed_during_read")
        require(os.path.samestat(after, path.lstat()),
                "file_replaced_during_read")
    return h.hexdigest(), raw


def validate_manifest(manifest):
    require(isinstance(manifest, dict) and set(manifest) == {"schema", "source", "artifacts"},
            "invalid_manifest_fields")
    require(type(manifest["schema"]) is int and manifest["schema"] == 1, "unsupported_schema")
    require(source_identity(manifest["source"]), "invalid_source_identity")
    artifacts = manifest["artifacts"]
    require(isinstance(artifacts, list) and 0 < len(artifacts) <= MAX_ARTIFACTS,
            "invalid_artifact_count")
    ids = set()
    required = {"id", "path", "target"}
    optional = {"expectedSha256", "observedSha256", "stamp", "build", "release"}
    for entry in artifacts:
        require(isinstance(entry, dict) and required <= set(entry) <= required | optional,
                "invalid_artifact_fields")
        require(token(entry["id"]) and entry["id"] not in ids, "invalid_or_duplicate_artifact_id")
        ids.add(entry["id"])
        require(explicit_path(entry["path"]), "invalid_artifact_path")
        require(target_identity(entry["target"]), "invalid_artifact_target")
        for field in ("expectedSha256", "observedSha256"):
            if field in entry:
                require(oid(entry[field], (64,)), "invalid_artifact_digest")
        for field in ("stamp", "build", "release"):
            if field in entry:
                ref = entry[field]
                require(isinstance(ref, dict) and {"path"} <= set(ref) <= {"path", "sha256"}
                        and explicit_path(ref["path"]), "invalid_receipt_reference")
                if "sha256" in ref:
                    require(oid(ref["sha256"], (64,)), "invalid_receipt_digest")
    return manifest


def result(status, *reasons, **data):
    return {"status": status, "reasons": list(reasons), **data}


def combined(*checks):
    states = [c["status"] for c in checks]
    status = "mismatch" if "mismatch" in states else (
        "unknown" if "unknown" in states else "verified")
    return result(status, *(reason for check in checks for reason in check["reasons"]))


def equals(value, expected, label):
    if value is None or expected is None:
        return result("unknown", label + "_missing")
    if type(value) is not type(expected) or value != expected:
        return result("mismatch", label + "_mismatch")
    return result("verified")


def git_source(repo, source):
    if repo is None:
        return result("unknown", "local_git_repository_not_supplied", **source)
    # No fetch, no replacement objects, no inherited Git directory or object
    # overrides. This is a local object lookup, not a current cleanliness check.
    env = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
    env.update(GIT_OPTIONAL_LOCKS="0", GIT_NO_LAZY_FETCH="1",
               GIT_ALLOW_PROTOCOL="", GIT_TERMINAL_PROMPT="0",
               GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)
    def git(*args):
        return subprocess.run(["git", "--no-replace-objects", "-C", str(repo), *args],
                              env=env, capture_output=True, text=True, timeout=10,
                              check=True).stdout.strip()
    try:
        commit = git("rev-parse", "--verify", source["commit"] + "^{commit}")
        tree = git("rev-parse", "--verify", source["commit"] + "^{tree}")
        if commit != source["commit"] or tree != source["tree"]:
            return result("mismatch", "git_commit_tree_mismatch", **source)
        if git("cat-file", "-t", tree) != "tree":
            return result("mismatch", "git_object_not_tree", **source)
        return result("verified", **source)
    except (OSError, subprocess.SubprocessError, UnicodeError):
        return result("unknown", "git_objects_unavailable_locally", **source)


def load_receipt(root, ref, kind):
    if ref is None:
        return None, result("unknown", kind + "_receipt_not_supplied")
    expected = ref.get("sha256")
    try:
        actual, raw = file_observation(root / ref["path"], json_input=True)
    except (OSError, InvalidEvidence):
        return None, result("unknown", kind + "_receipt_unreadable", expectedSha256=expected)
    evidence = {"actualSha256": actual, "expectedSha256": expected}
    integrity = equals(actual, expected, kind + "_receipt_digest")
    if integrity["status"] != "verified":
        return None, {**integrity, **evidence}
    try:
        value = parse_json(raw)
        require(isinstance(value, dict) and type(value.get("schema")) is int
                and value["schema"] == 1 and value.get("kind") == kind,
                "invalid_receipt_header")
    except InvalidEvidence:
        return None, result("unknown", kind + "_receipt_invalid", **evidence)
    return value, result("verified", **evidence)


def terminal(receipt, label):
    if receipt.get("state") in (None, "unknown", "started"):
        return result("unknown", label + "_terminal_unknown")
    return combined(equals(receipt.get("state"), "exited", label + "_terminal_state"),
                    equals(receipt.get("exitCode"), 0, label + "_exit_code"))


def checkout(receipt, source, phase):
    label = "checkout_" + phase
    if receipt is None:
        return result("unknown", label + "_missing")
    if not isinstance(receipt, dict):
        return result("unknown", label + "_invalid")
    return combined(equals(receipt.get("phase"), phase, label + "_phase"),
                    equals(receipt.get("source"), source, label + "_source"),
                    equals(receipt.get("status"), "clean", label + "_status"),
                    equals(receipt.get("method"), "git-status-porcelain-v1-untracked-all",
                           label + "_method"))


def stamp_check(stamp, provenance, source, actual):
    if stamp is None:
        return combined(provenance)
    return combined(provenance,
                    equals(stamp.get("method"), "dotnet-assembly-informational-version",
                           "stamp_method"),
                    equals(stamp.get("artifactSha256"), actual, "stamp_artifact"),
                    equals(stamp.get("commit"), source["commit"], "stamp_commit"))


def build_checks(build, build_receipt, release, release_receipt, source, source_check,
                 target, actual):
    checks = [source_check, build_receipt, release_receipt]
    build_id = None
    sdk = None
    if build is not None:
        build_id = build.get("buildId") if token(build.get("buildId")) else None
        sdk = build.get("sdkVersion") if token(build.get("sdkVersion")) else None
        checks.extend([
            result("verified") if build_id and sdk else result("unknown", "build_identity_missing"),
            terminal(build, "build"),
            equals(build.get("source"), source, "build_source"),
            equals(build.get("target"), target, "build_target"),
            equals(build.get("artifactSha256"), actual, "build_artifact"),
            checkout(build.get("checkoutBefore"), source, "before-build"),
            checkout(build.get("checkoutAfter"), source, "after-build"),
        ])
    if release is not None:
        checks.extend([
            terminal(release, "release"),
            equals(release.get("source"), source, "release_source"),
            equals(release.get("target"), target, "release_target"),
            equals(release.get("artifactSha256"), actual, "release_artifact"),
            equals(release.get("buildId"), build_id, "release_build_id"),
            equals(release.get("buildReceiptSha256"), build_receipt.get("actualSha256"),
                   "release_build_receipt"),
        ])
    return combined(*checks), build_id, sdk


def verify_artifact(root, entry, source, source_check):
    actual = None
    try:
        actual, _ = file_observation(root / entry["path"])
        bytes_check = equals(actual, entry.get("expectedSha256"), "expected_artifact_digest")
        observed_check = equals(actual, entry.get("observedSha256"), "observed_artifact_digest")
    except (OSError, InvalidEvidence):
        bytes_check = observed_check = result("unknown", "artifact_unreadable_or_changing")
    stamp, stamp_receipt = load_receipt(root, entry.get("stamp"), "source-stamp")
    build, build_receipt = load_receipt(root, entry.get("build"), "build")
    release, release_receipt = load_receipt(root, entry.get("release"), "release")
    stamp_result = stamp_check(stamp, stamp_receipt, source, actual)
    build_result, build_id, sdk = build_checks(
        build, build_receipt, release, release_receipt, source, source_check,
        entry["target"], actual)
    return {
        "id": entry["id"], "target": entry["target"],
        "status": combined(bytes_check, observed_check, stamp_result, build_result)["status"],
        "expectedSha256": entry.get("expectedSha256"),
        "observedSha256": entry.get("observedSha256"), "actualSha256": actual,
        "artifactBytes": bytes_check, "observedBytes": observed_check,
        "sourceStamp": {**stamp_result,
                        "commit": stamp.get("commit") if stamp and oid(stamp.get("commit"), (40, 64)) else None,
                        "basis": "digest-bound-receipt-not-direct-extraction"},
        "buildProvenance": build_result, "buildId": build_id, "sdkVersion": sdk,
        "receipts": {"stamp": stamp_receipt, "build": build_receipt, "release": release_receipt},
    }


def verify_manifest(path, repo=None):
    manifest_digest, raw = file_observation(path, json_input=True)
    manifest = validate_manifest(parse_json(raw))
    source = manifest["source"]
    source_check = git_source(repo, source)
    artifacts = [verify_artifact(path.parent, entry, source, source_check)
                 for entry in manifest["artifacts"]]
    return {"schema": 1, "status": combined(source_check, *[
                result(a["status"]) for a in artifacts])["status"],
            "trustBoundary": "caller-pinned-receipts",
            "manifestSha256": manifest_digest,
            "source": source_check, "artifacts": artifacts}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--repo", type=Path, help="local Git object database; never fetched")
    args = parser.parse_args()
    try:
        report = verify_manifest(args.manifest, args.repo)
    except InvalidEvidence as exc:
        report = result("invalid", str(exc))
    except OSError:
        report = result("invalid", "manifest_unreadable")
    print(json.dumps(report, sort_keys=True, indent=2))
    return {"verified": 0, "mismatch": 1, "unknown": 2, "invalid": 2}[report["status"]]


if __name__ == "__main__":
    sys.exit(main())
