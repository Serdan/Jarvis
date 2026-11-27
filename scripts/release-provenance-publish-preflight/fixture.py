#!/usr/bin/env python3
"""Disposable actual publisher boundary witness. No real build or release."""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import uuid

MAIN = "291b5499051fbddfff1c76308e1a0202a5d1862f"
TREE = "fc4aaf661d434c479b403d09ee1535e3d58d3442"
HELPER = "92bcb1a97a5a8d27f36eb535553961c4cabb4ce9"
SOURCE = {"commit": MAIN, "tree": TREE}
CLIENT_RIDS = ("linux-x64", "win-x64", "osx-arm64")
SERVER_URL = "https://jarvis2.kehlet.dev/client"
SCRIPT = Path(__file__).resolve()

def need(value, message):
    if not value:
        raise RuntimeError(message)

def run(argv, cwd, env, timeout=90):
    return subprocess.run([str(x) for x in argv], cwd=cwd, env=env,
                          stdin=subprocess.DEVNULL, capture_output=True,
                          timeout=timeout)

def output(argv, cwd, env):
    p = run(argv, cwd, env)
    need(p.returncode == 0, f"command failed {argv}: {p.stderr.decode(errors='replace')}")
    return p.stdout

def git(repo, env, *args):
    return output(["git", "--no-replace-objects", *args], repo, env)

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def write_json(path, value):
    path.write_text(json.dumps(value, sort_keys=True) + "\n", encoding="utf-8")
    return {"path": str(path), "sha256": digest(path)}

def receipt_bundle(evidence, repo, publisher, scenario):
    """Emit exact synthetic receipt bytes and observed publisher output before cleanup."""
    files = {}
    for path in sorted(evidence.glob("*.json")):
        files[path.name] = {"sha256": digest(path), "rawUtf8": path.read_text()}
    downloads = repo / "artifacts/downloads"
    copied = {}
    if downloads.is_dir():
        for path in sorted(downloads.iterdir()):
            copied[path.name] = {"sha256": digest(path),
                                 "rawBase64": base64.b64encode(path.read_bytes()).decode()}
    print(json.dumps({"receiptBundle": scenario, "files": files, "downloads": copied,
                      "publisherStdout": publisher.stdout.decode(errors="replace"),
                      "publisherStderr": publisher.stderr.decode(errors="replace")}), flush=True)

def observation(repo, env, phase):
    commit, tree = git(repo, env, "rev-parse", "HEAD", "HEAD^{tree}").decode().splitlines()
    dirty = git(repo, env, "status", "--porcelain=v1", "--untracked-files=all")
    return {"phase": phase, "source": {"commit": commit, "tree": tree},
            "status": "dirty" if dirty else "clean",
            "method": "git-status-porcelain-v1-untracked-all"}

def target_for(product, rid):
    return {"product": product, "rid": rid, "configuration": "Release"}

def names(target):
    product, rid = target["product"], target["rid"]
    ext = ".exe" if rid.startswith("win-") else ""
    kind = "client" if product == "JarvisClient" else "server"
    return Path("artifacts") / kind / rid / (product + ext), product + "-" + rid + ext

def occurrence(nonce, target):
    return nonce + "-" + target["product"] + "-" + target["rid"]

def fake_dotnet(args):
    # Only the actual publisher's nested build.cs invocation can enter this shim.
    repo = Path.cwd()
    env = os.environ.copy()
    evidence = Path(env["FIXTURE_EVIDENCE"])
    root = Path(env["FIXTURE_ROOT"]).resolve()
    need(root.parent.name.startswith("jarvis-publisher-fixture-")
         and repo == root / "repo" and evidence == root / "evidence",
         "fake build may only run inside the disposable fixture")
    nonce, scenario = env["FIXTURE_NONCE"], env["FIXTURE_SCENARIO"]
    need(len(args) >= 4 and args[0] == "scripts/build.cs" and args[2] == "--rid",
         "unexpected nested dotnet invocation")
    rid = args[3]
    if args[1] == "publish-client":
        need(rid in CLIENT_RIDS and args == ["scripts/build.cs", "publish-client",
             "--rid", rid, "--server", SERVER_URL], "unexpected client argv")
        target = target_for("JarvisClient", rid)
    else:
        need(args == ["scripts/build.cs", "publish-server", "--rid", "linux-x64"],
             "unexpected server argv")
        target = target_for("JarvisServer", rid)
    rel, name = names(target)
    artifact = repo / rel
    before = observation(repo, env, "before-build")
    event = evidence / (name + ".invocation.json")
    need(not event.exists(), "duplicate nested invocation")
    if scenario == "stale":
        # Deliberately reproduce success-without-fresh-output behavior.
        need(artifact.is_file(), "missing stale fixture")
        code = 0
    else:
        need(not artifact.exists(), "fake build requires fresh output")
        artifact.parent.mkdir(parents=True, exist_ok=True)
        child = run([sys.executable, "-c",
                     "import pathlib,sys; pathlib.Path(sys.argv[1]).write_bytes(sys.argv[2].encode()); sys.exit(int(sys.argv[3]))",
                     artifact, "FAKE ONLY " + MAIN + " " + name,
                     7 if scenario == "build-failed" else 0], repo, env)
        code = child.returncode
    after = observation(repo, env, "after-build")
    invocation = {"argv": args, "nonce": nonce, "state": "exited", "exitCode": code,
                  "before": before, "after": after, "simulated": True}
    if code == 0 and scenario not in ("stale", "absent"):
        sha = digest(artifact)
        build = {"schema": 1, "kind": "build", "buildId": occurrence(nonce, target),
                 "sdkVersion": env["FIXTURE_SDK"], "source": SOURCE, "target": target,
                 "state": "exited", "exitCode": 0, "artifactSha256": sha,
                 "checkoutBefore": before, "checkoutAfter": after,
                 "fixtureOnly": True}
        stamp = {"schema": 1, "kind": "source-stamp", "artifactSha256": sha,
                 "commit": MAIN, "method": "dotnet-assembly-informational-version",
                 "fixtureOnly": True}  # synthetic, NOT an assembly inspection
        invocation["receiptPins"] = {
            "build": write_json(evidence / (name + ".build.json"), build)["sha256"],
            "stamp": write_json(evidence / (name + ".stamp.json"), stamp)["sha256"]}
    write_json(event, invocation)
    return code

def checksum_entries(path):
    entries = {}
    for line in path.read_text().splitlines():
        sha, name = line.split("  ", 1)
        need(re.fullmatch("[0-9a-f]{64}", sha) and name not in entries, "invalid checksum entry")
        entries[name] = sha
    return entries

def fixture_case(root, origin, helper, real_dotnet, env, scenario, product, stale_evidence=None):
    repo, evidence = root / "repo", root / "evidence"
    evidence.mkdir(parents=True)
    # Local-only clone; no remote GitHub operation, no changes to source checkout.
    output(["git", "-c", "protocol.file.allow=always", "clone", "--shared",
            "--no-checkout", "--", origin, repo], root, env)
    output(["git", "-c", "core.hooksPath=" + os.devnull, "checkout", "--detach", MAIN], repo, env)
    first = observation(repo, env, "before-build")
    need(first["source"] == SOURCE and first["status"] == "clean", "source checkout not exact/clean")
    targets = [target_for(product, rid) for rid in
               (CLIENT_RIDS if product == "JarvisClient" else ("linux-x64",))]
    nonce = uuid.uuid4().hex
    if scenario == "dirty":
        with (repo / "Jarvis.slnx").open("ab") as f:
            f.write(b"\n<!-- disposable dirty control -->\n")
    if scenario == "stale":
        need(stale_evidence is not None, "missing prior occurrence")
        for target in targets:
            rel, name = names(target)
            (repo / rel).parent.mkdir(parents=True)
            (repo / rel).write_bytes(("FAKE ONLY " + MAIN + " " + name).encode())
            for kind in ("build", "stamp"):
                shutil.copyfile(stale_evidence / (name + "." + kind + ".json"),
                                evidence / (name + "." + kind + ".json"))
    # Unchanged actual publisher, invoked with real SDK; only nested dotnet is a shim.
    publisher = "scripts/publish-client-downloads.cs" if product == "JarvisClient" else "scripts/publish-server-download.cs"
    need((repo / publisher).read_bytes() == git(origin, env, "show", MAIN + ":" + publisher),
         "publisher source changed")
    shim = root / "bin"
    shim.mkdir()
    shim_path = shim / "dotnet"
    shim_path.write_text("#!/bin/sh\nexec " + shlex.quote(sys.executable) + " -B " +
                         shlex.quote(str(SCRIPT)) + ' --fake-dotnet "$@"\n')
    shim_path.chmod(0o700)
    child_env = dict(env, PATH=str(shim) + os.pathsep + env["PATH"],
                     FIXTURE_EVIDENCE=str(evidence), FIXTURE_NONCE=nonce,
                     FIXTURE_SCENARIO=scenario, FIXTURE_ROOT=str(root))
    # Qualification placeholder is a real observed subprocess exit, but NOT real tests.
    qual_argv = [sys.executable, "-c", "pass"]
    qual_before = observation(repo, env, "before-qualification")
    qualification = run(qual_argv, repo, env)
    qual_after = observation(repo, env, "after-qualification")
    qual_receipt = {"argv": qual_argv, "source": SOURCE, "nonce": nonce, "state": "exited",
                    "exitCode": qualification.returncode, "fixtureOnly": True,
                    "before": qual_before, "after": qual_after}
    qual_path = evidence / "qualification.json"
    qualification_ref = write_json(qual_path, qual_receipt)
    if scenario == "qualification-absent":
        qual_path.unlink()
    # .NET 11 file apps default to AOT packages unavailable in offline restore.
    # Managed Release execution preserves the unchanged publisher's process boundary.
    argv = [str(real_dotnet), "run", "--file", publisher, "-c", "Release",
            "-p:PublishAot=false", "-p:SelfContained=false", "--disable-build-servers"]
    p = run(argv, repo, child_env)
    publisher_ref = write_json(evidence / "publisher.json",
        {"argv": argv, "source": SOURCE, "nonce": nonce,
         "state": "exited", "exitCode": p.returncode,
         "publisherSha256": digest(repo / publisher), "fixtureOnly": True})
    invocations = sorted(evidence.glob("*.invocation.json"))
    if p.returncode != 0:
        print(json.dumps({"scenario": scenario, "product": product, "publisherExit": p.returncode,
                          "invocations": len(invocations), "publisherReceipt": publisher_ref["sha256"],
                          "gate": "rejected"}), flush=True)
        need(scenario == "build-failed" and p.returncode == 7 and len(invocations) == 1,
             "unexpected publisher failure: " + p.stdout.decode(errors="replace") + p.stderr.decode(errors="replace"))
        need(not (repo / "artifacts/downloads").exists()
             or not any((repo / "artifacts/downloads").iterdir()), "failed build produced downloads")
        receipt_bundle(evidence, repo, p, scenario)
        return evidence
    need(scenario != "build-failed", "publisher hid nonzero build exit")
    need(len(invocations) == len(targets), "actual nested invocation count differs")
    downloads = repo / "artifacts/downloads"
    checksums = checksum_entries(downloads / "SHA256SUMS")
    need(set(checksums) == {names(t)[1] for t in targets}, "unexpected release batch members")
    if scenario == "substitution":
        (downloads / names(targets[0])[1]).write_bytes(b"substituted after publisher hash")
    manifest = {"schema": 1, "source": SOURCE, "artifacts": []}
    qual_verified = (qual_path.is_file() and digest(qual_path) == qualification_ref["sha256"]
                     and json.loads(qual_path.read_text()) == qual_receipt
                     and qualification.returncode == 0
                     and all(obs["status"] == "clean" and obs["source"] == SOURCE
                             for obs in (qual_before, qual_after)))
    fresh = first["source"] == SOURCE
    proofs = []
    for target in targets:
        rel, name = names(target)
        entry = {"id": name, "path": str(downloads / name), "target": target,
                 "expectedSha256": checksums[name],
                 "observedSha256": digest(downloads / name)}
        invocation_path = evidence / (name + ".invocation.json")
        invocation = json.loads(invocation_path.read_text())
        fresh &= (invocation["nonce"] == nonce and invocation["state"] == "exited"
                  and invocation["exitCode"] == 0)
        build_path = evidence / (name + ".build.json")
        stamp_path = evidence / (name + ".stamp.json")
        if build_path.exists() and stamp_path.exists():
            build = json.loads(build_path.read_text())
            entry["build"] = {"path": str(build_path), "sha256": digest(build_path)}
            entry["stamp"] = {"path": str(stamp_path), "sha256": digest(stamp_path)}
            fresh &= (build["buildId"] == occurrence(nonce, target)
                      and invocation.get("receiptPins") ==
                          {k: entry[k]["sha256"] for k in ("build", "stamp")})
            entry["release"] = write_json(evidence / (name + ".release.json"),
                {"schema": 1, "kind": "release", "buildId": build["buildId"],
                 "buildReceiptSha256": entry["build"]["sha256"], "source": SOURCE,
                 "target": target, "artifactSha256": entry["observedSha256"],
                 "state": "exited", "exitCode": p.returncode,
                 "qualificationReceiptSha256": qualification_ref["sha256"],
                 "publisherReceiptSha256": publisher_ref["sha256"], "fixtureOnly": True})
        else:
            fresh = False
        proofs.append({"name": name, "expected": checksums[name], "observed": entry["observedSha256"],
                       "invocation": digest(invocation_path),
                       "receiptPins": {k: entry[k]["sha256"] for k in ("build", "stamp", "release") if k in entry}})
        manifest["artifacts"].append(entry)
    manifest_path = evidence / "manifest.json"
    manifest_ref = write_json(manifest_path, manifest)
    verifier = run([sys.executable, "-B", helper, manifest_path, "--repo", repo], repo, env)
    report = json.loads(verifier.stdout)
    expected_helper = {"positive": 0, "stale": 0, "qualification-absent": 0,
                       "absent": 2, "dirty": 1, "substitution": 1}[scenario]
    accepted = bool(fresh and qual_verified and verifier.returncode == 0 and report["status"] == "verified")
    print(json.dumps({"scenario": scenario, "product": product, "nonce": nonce,
                      "publisherExit": p.returncode, "invocations": len(invocations),
                      "helperExit": verifier.returncode, "helperStatus": report["status"],
                      "freshOccurrence": bool(fresh), "qualificationVerified": qual_verified,
                      "gate": "accepted" if accepted else "rejected",
                      "manifest": manifest_ref["sha256"], "qualification": qualification_ref["sha256"],
                      "publisherReceipt": publisher_ref["sha256"], "artifacts": proofs}), flush=True)
    write_json(evidence / "verifier-report.json", report)
    receipt_bundle(evidence, repo, p, scenario)
    need(verifier.returncode == expected_helper, "unexpected helper result: " + verifier.stdout.decode())
    need(accepted == (scenario == "positive"), "unexpected gate decision")
    return evidence

def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--fake-dotnet":
        return fake_dotnet(sys.argv[2:])
    need(len(sys.argv) == 1, "no user-supplied output or production paths accepted")
    need(sys.platform.startswith("linux"), "fixture requires Linux")
    origin = SCRIPT.parents[2]
    real_dotnet = Path(shutil.which("dotnet") or "").resolve()
    need(real_dotnet.is_file(), "real dotnet SDK unavailable")
    with tempfile.TemporaryDirectory(prefix="jarvis-publisher-fixture-") as tmp:
        root = Path(tmp)
        home = root / "home"
        home.mkdir()
        (root / "NuGet.Config").write_text("<configuration><packageSources><clear /></packageSources></configuration>")
        env = {"PATH": os.environ["PATH"], "HOME": str(home), "DOTNET_CLI_HOME": str(home),
               "DOTNET_ROOT": str(real_dotnet.parent), "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
               "DOTNET_NOLOGO": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
               "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE": "true", "NUGET_CERT_REVOCATION_MODE": "offline",
               "NUGET_PACKAGES": str(root / "packages"), "GIT_CONFIG_NOSYSTEM": "1",
               "GIT_CONFIG_GLOBAL": os.devnull, "GIT_NO_LAZY_FETCH": "1",
               "GIT_ALLOW_PROTOCOL": "file", "GIT_TERMINAL_PROMPT": "0", "LC_ALL": "C.UTF-8"}
        env["FIXTURE_SDK"] = output([real_dotnet, "--version"], root, env).decode().strip()
        need(git(origin, env, "rev-parse", MAIN + "^{tree}").decode().strip() == TREE, "wrong source tree")
        need(git(origin, env, "rev-parse", HELPER + "^{commit}").decode().strip() == HELPER, "wrong helper commit")
        helper = root / "verify.py"
        helper.write_bytes(git(origin, env, "show", HELPER + ":scripts/release-provenance/verify.py"))
        print(json.dumps({"source": SOURCE, "helperCommit": HELPER, "helperSha256": digest(helper),
                          "sdk": env["FIXTURE_SDK"], "fixtureOnly": True,
                          "harnessSha256": digest(SCRIPT)}), flush=True)
        prior = fixture_case(root / "server-positive", origin, helper, real_dotnet, env, "positive", "JarvisServer")
        fixture_case(root / "client-positive", origin, helper, real_dotnet, env, "positive", "JarvisClient")
        for scenario in ("absent", "qualification-absent", "dirty", "stale", "substitution", "build-failed"):
            fixture_case(root / scenario, origin, helper, real_dotnet, env, scenario, "JarvisServer", prior)
    print("PASS: actual publisher command boundary, synthetic evidence only", flush=True)
    return 0

if __name__ == "__main__":
    sys.exit(main())
