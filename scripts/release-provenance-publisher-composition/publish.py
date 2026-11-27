#!/usr/bin/env python3
"""Local download publisher with a caller-pinned qualification/stamp boundary."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import uuid

# Use the exact reviewed verifier, including its bounded parser and file reader.
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "release-provenance"))
import verify as v

CLIENT_RIDS = ("linux-x64", "win-x64", "osx-arm64")
SERVER_URL = "https://jarvis2.kehlet.dev/client"


def json_write(path, value):
    path.write_text(json.dumps(value, sort_keys=True) + "\n", encoding="utf-8")
    return {"path": str(path), "sha256": v.file_observation(path)[0]}


def pinned_copy(ref, base, destination):
    v.require(isinstance(ref, dict) and set(ref) == {"path", "sha256"}
              and v.explicit_path(ref["path"]) and v.oid(ref["sha256"], (64,)),
              "invalid_caller_receipt_reference")
    sha, raw = v.file_observation(base / ref["path"], json_input=True)
    v.require(sha == ref["sha256"], "caller_receipt_digest_mismatch")
    destination.write_bytes(raw)
    return {"path": str(destination), "sha256": sha}


def local_git(root, *args):
    env = {k: val for k, val in os.environ.items() if not k.startswith("GIT_")}
    env.update(GIT_OPTIONAL_LOCKS="0", GIT_NO_LAZY_FETCH="1",
               GIT_ALLOW_PROTOCOL="", GIT_TERMINAL_PROMPT="0",
               GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)
    return subprocess.run(
        ["git", "--no-replace-objects", "-c", "core.fsmonitor=false", "-C", str(root), *args],
        env=env, capture_output=True, check=True, timeout=30).stdout


def observation(root, source, phase):
    actual_root = Path(os.fsdecode(local_git(root, "rev-parse", "--show-toplevel").rstrip(b"\r\n")))
    v.require(actual_root.resolve() == root, "not_repository_root")
    commit, tree = local_git(root, "rev-parse", "HEAD", "HEAD^{tree}").decode("ascii").splitlines()
    dirty = local_git(root, "status", "--porcelain=v1", "--untracked-files=all")
    value = {"phase": phase, "source": {"commit": commit, "tree": tree},
             "status": "dirty" if dirty else "clean",
             "method": "git-status-porcelain-v1-untracked-all"}
    v.require(v.checkout(value, source, phase)["status"] == "verified",
              "checkout_not_clean_exact_source")
    return value


def target_names(product, rid):
    suffix = ".exe" if rid.startswith("win-") else ""
    return product + suffix, product + "-" + rid + suffix


def safe_directory(path):
    v.require(not path.is_symlink(), "symlink_directory_not_supported")
    path.mkdir(exist_ok=True)
    v.require(path.is_dir() and not path.is_symlink(), "invalid_output_directory")


def qualification(base, ref, destination, source):
    pin = pinned_copy(ref, base, destination)
    receipt, integrity = v.load_receipt(destination.parent, pin, "qualification")
    v.require(integrity["status"] == "verified" and receipt is not None,
              "qualification_receipt_invalid")
    v.require(v.token(receipt.get("nonce")) and v.combined(
        v.terminal(receipt, "qualification"),
        v.equals(receipt.get("source"), source, "qualification_source"),
        v.checkout(receipt.get("before"), source, "before-qualification"),
        v.checkout(receipt.get("after"), source, "after-qualification")
    )["status"] == "verified", "qualification_not_terminal_clean_exact_source")
    return pin


def verify_batch(session, source, entries, root, filename):
    manifest = session / filename
    json_write(manifest, {"schema": 1, "source": source, "artifacts": entries})
    report = v.verify_manifest(manifest, root)
    json_write(session / (filename + ".report.json"), report)
    v.require(report["status"] == "verified", "release_evidence_not_verified")


def checksums(product, downloads, entries):
    new = {entry["id"]: entry["expectedSha256"] for entry in entries}
    if product == "JarvisClient":
        # Preserve the client's existing inventory semantics: include other
        # download files, without making a provenance claim about those files.
        for path in downloads.iterdir():
            if path.name not in new and path.name != "SHA256SUMS" and path.is_file():
                new[path.name] = v.file_observation(path)[0]
        return [f"{sha}  {name}" for name, sha in sorted(new.items())]
    # Preserve the server publisher's existing unrelated checksum entries.
    manifest = downloads / "SHA256SUMS"
    lines = []
    if manifest.exists() or manifest.is_symlink():
        _, raw = v.file_observation(manifest, json_input=True)
        lines = [line for line in raw.decode("utf-8").splitlines()
                 if not any(line.endswith("  " + name) for name in new)]
    return sorted(lines + [f"{sha}  {name}" for name, sha in new.items()])


def publish(root, product, evidence, evidence_sha):
    v.require(v.oid(evidence_sha, (64,)), "invalid_evidence_digest")
    actual_sha, raw = v.file_observation(evidence, json_input=True)
    v.require(actual_sha == evidence_sha, "caller_evidence_digest_mismatch")
    descriptor = v.parse_json(raw)
    v.require(isinstance(descriptor, dict)
              and set(descriptor) == {"schema", "source", "qualification", "stamps"}
              and type(descriptor["schema"]) is int and descriptor["schema"] == 1
              and v.source_identity(descriptor["source"]), "invalid_publisher_evidence")
    source = descriptor["source"]
    rids = CLIENT_RIDS if product == "JarvisClient" else ("linux-x64",)
    names = {target_names(product, rid)[1] for rid in rids}
    stamps = descriptor["stamps"]
    v.require(isinstance(stamps, dict) and set(stamps) == names, "invalid_stamp_inventory")
    observation(root, source, "before-publisher")

    artifacts = root / "artifacts"
    safe_directory(artifacts)
    evidence_root = artifacts / "release-provenance"
    safe_directory(evidence_root)
    # A fresh occurrence and isolated output directories exclude success with
    # stale output; never replay a failed/unknown occurrence or reuse its files.
    session = Path(tempfile.mkdtemp(prefix="publisher-", dir=evidence_root))
    print("Publisher evidence: " + str(session), flush=True)
    (session / "caller.json").write_bytes(raw)
    qual = qualification(evidence.parent, descriptor["qualification"],
                         session / "qualification.json", source)
    stamp_pins = {}
    for name in sorted(names):
        pin = pinned_copy(stamps[name], evidence.parent, session / (name + ".stamp.json"))
        stamp, integrity = v.load_receipt(session, pin, "source-stamp")
        v.require(integrity["status"] == "verified" and stamp is not None
                  and v.oid(stamp.get("artifactSha256"), (64,))
                  and v.stamp_check(stamp, integrity, source, stamp["artifactSha256"])["status"] == "verified",
                  "stamp_receipt_invalid")
        stamp_pins[name] = pin

    sdk = subprocess.run(["dotnet", "--version"], cwd=root, capture_output=True,
                         check=True, text=True).stdout.strip()
    v.require(v.token(sdk), "invalid_sdk_version")
    occurrence = uuid.uuid4().hex
    entries = []
    staged = session / "staged"
    staged.mkdir()
    for rid in rids:
        filename, name = target_names(product, rid)
        target = {"product": product, "rid": rid, "configuration": "Release"}
        build_id = occurrence + "-" + product + "-" + rid
        output = session / ("build-" + rid)
        argv = ["dotnet", "scripts/build.cs",
                "publish-client" if product == "JarvisClient" else "publish-server",
                "--rid", rid]
        if product == "JarvisClient":
            argv += ["--server", SERVER_URL]
        argv += ["--output", str(output)]
        before = observation(root, source, "before-build")
        v.require(not output.exists() and not output.is_symlink(), "build_output_not_fresh")
        print("Building " + name, flush=True)
        invocation_path = session / (name + ".invocation.json")
        invocation = {"schema": 1, "buildId": build_id, "source": source, "target": target,
                      "argv": argv, "checkoutBefore": before, "state": "started"}
        json_write(invocation_path, invocation)
        code = subprocess.run(argv, cwd=root).returncode
        invocation.update(state="exited", exitCode=code)
        json_write(invocation_path, invocation)
        if code != 0:
            return code if code > 0 else 1
        after = observation(root, source, "after-build")
        v.require(output.is_dir() and not output.is_symlink(), "build_output_missing")
        built = output / filename
        digest = v.file_observation(built)[0]
        build_pin = json_write(session / (name + ".build.json"),
            {"schema": 1, "kind": "build", "buildId": build_id, "sdkVersion": sdk,
             "source": source, "target": target, "state": "exited", "exitCode": code,
             "artifactSha256": digest, "checkoutBefore": before, "checkoutAfter": after,
             "argv": argv})
        copied = staged / name
        shutil.copy2(built, copied)
        if product == "JarvisClient" and not rid.startswith("win-") and os.name != "nt":
            copied.chmod(0o755)
        observed = v.file_observation(copied)[0]
        release_pin = json_write(session / (name + ".stage-release.json"),
            {"schema": 1, "kind": "release", "buildId": build_id,
             "buildReceiptSha256": build_pin["sha256"], "source": source, "target": target,
             "scope": "staged-copy",
             "artifactSha256": observed, "state": "exited", "exitCode": 0,
             "qualificationReceiptSha256": qual["sha256"],
             "callerEvidenceSha256": evidence_sha})
        entries.append({"id": name, "path": str(copied), "target": target,
                        "expectedSha256": digest, "observedSha256": observed,
                        "stamp": stamp_pins[name], "build": build_pin, "release": release_pin})
    observation(root, source, "before-release")
    verify_batch(session, source, entries, root, "staged-manifest.json")

    # No final download or checksum mutation before the entire batch verifies.
    downloads = artifacts / "downloads"
    safe_directory(downloads)
    lines = checksums(product, downloads, entries)
    checksum = session / "SHA256SUMS"
    checksum.write_text("\n".join(lines) + "\n", encoding="utf-8")
    for entry in entries:
        destination = downloads / entry["id"]
        os.replace(entry["path"], destination)
        entry["path"] = str(destination)
        # A distinct receipt records the final copy, not merely a staged copy.
        stage_sha, stage_raw = v.file_observation(Path(entry["release"]["path"]), json_input=True)
        v.require(stage_sha == entry["release"]["sha256"], "staged_receipt_changed")
        receipt = v.parse_json(stage_raw)
        receipt.update(scope="final-copy", artifactSha256=v.file_observation(destination)[0])
        entry["release"] = json_write(session / (entry["id"] + ".release.json"), receipt)
    observation(root, source, "after-release-copy")
    verify_batch(session, source, entries, root, "manifest.json")
    # Only emit the new checksum after the actual final copies verify.
    os.replace(checksum, downloads / "SHA256SUMS")
    print("Verified local downloads: " + ", ".join(sorted(names)), flush=True)
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("product", choices=("JarvisClient", "JarvisServer"))
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--evidence-sha256", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    try:
        return publish(root, args.product, args.evidence.absolute(), args.evidence_sha256)
    except (OSError, ValueError, UnicodeError, subprocess.SubprocessError) as exc:
        print("Publisher rejected evidence or failed locally: " + str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
