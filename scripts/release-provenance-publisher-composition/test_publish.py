#!/usr/bin/env python3
"""Exercise real C# publisher entry points; only build/stamp/qualification are synthetic."""
import hashlib
import json
import os
from pathlib import Path
import shlex
import shutil
import sys
import tempfile

SCRIPT = Path(__file__).resolve()
sys.path.insert(0, str(SCRIPT.parents[1] / "release-provenance-publish-preflight"))
from fixture import (run, output, git, need, digest, write_json, target_for,
                     names, observation, CLIENT_RIDS, SERVER_URL)


def fake_bytes(source, name):
    return ("FAKE ONLY " + source["commit"] + " " + name).encode()


def fake_dotnet(args):
    env = os.environ
    root = Path(env["COMPOSED_FIXTURE_ROOT"]).resolve()
    repo = root / "repo"
    need(root.parent.name.startswith("jarvis-composed-publisher-") and Path.cwd() == repo,
         "shim only runs in disposable fixture")
    if args == ["--version"]:
        print(env["COMPOSED_FIXTURE_SDK"])
        return 0
    need(len(args) >= 6 and args[0] == "scripts/build.cs" and args[2] == "--rid"
         and args[-2] == "--output", "unexpected nested build invocation")
    rid = args[3]
    product = "JarvisClient" if args[1] == "publish-client" else "JarvisServer"
    expected = ["scripts/build.cs", "publish-client" if product == "JarvisClient" else
                "publish-server", "--rid", rid]
    if product == "JarvisClient":
        need(rid in CLIENT_RIDS, "unknown client RID")
        expected += ["--server", SERVER_URL]
    else:
        need(rid == "linux-x64", "unknown server RID")
    expected += ["--output", args[-1]]
    need(args == expected, "nested build argv changed")
    path = Path(args[-1])
    need(path.parent.parent == repo / "artifacts/release-provenance"
         and path.name == "build-" + rid and not path.exists(), "build not isolated/fresh")
    source = json.loads(env["COMPOSED_FIXTURE_SOURCE"])
    scenario = env["COMPOSED_FIXTURE_SCENARIO"]
    rel, name = names(target_for(product, rid))
    with (root / "invocations.jsonl").open("a") as f:
        f.write(json.dumps(args) + "\n")
    if scenario == "stale":
        need((repo / rel).is_file(), "missing old conventional build output")
        return 0
    if scenario == "build-failed" or (scenario == "late-client-failure" and rid == "win-x64"):
        return 7
    path.mkdir()
    artifact = path / rel.name
    artifact.write_bytes(b"substituted" if scenario == "substitution" else fake_bytes(source, name))
    if scenario == "dirty-after-build":
        with (repo / "Jarvis.slnx").open("ab") as f:
            f.write(b"\n<!-- dirty after build -->\n")
    return 0


def case(root, origin, source, real_dotnet, env, product, scenario):
    root.mkdir()
    repo, evidence = root / "repo", root / "evidence"
    evidence.mkdir()
    output(["git", "-c", "protocol.file.allow=always", "clone", "--shared",
            "--no-checkout", "--", origin, repo], root, env)
    output(["git", "-c", "core.hooksPath=" + os.devnull, "checkout", "--detach",
            source["commit"]], repo, env)
    before = observation(repo, env, "before-qualification")
    need(before["source"] == source and before["status"] == "clean", "wrong fixture checkout")
    # The placeholder is deliberately not real qualification.
    qual = run([sys.executable, "-c", "pass"], repo, env)
    after = observation(repo, env, "after-qualification")
    qual_value = {"schema": 1, "kind": "qualification", "source": source, "nonce": "fixture",
                  "state": "exited", "exitCode": qual.returncode, "before": before, "after": after,
                  "fixtureOnly": True}
    if scenario == "qualification-stale":
        qual_value["source"] = {"commit": "0" * len(source["commit"]), "tree": source["tree"]}
    elif scenario == "qualification-unknown":
        qual_value["state"] = "unknown"
    q = write_json(evidence / "qualification.json", qual_value)
    stamps = {}
    rids = CLIENT_RIDS if product == "JarvisClient" else ("linux-x64",)
    for rid in rids:
        rel, name = names(target_for(product, rid))
        value = {"schema": 1, "kind": "source-stamp", "commit": source["commit"],
                 "method": "dotnet-assembly-informational-version", "fixtureOnly": True,
                 "artifactSha256": hashlib.sha256(fake_bytes(source, name)).hexdigest()}
        if scenario == "malformed-stamp":
            stamp_path = evidence / (name + ".stamp.json")
            stamp_path.write_bytes(b'{"schema":1,"schema":1}')
            stamps[name] = {"path": str(stamp_path), "sha256": digest(stamp_path)}
        else:
            stamps[name] = write_json(evidence / (name + ".stamp.json"), value)
        if scenario == "stale":
            (repo / rel).parent.mkdir(parents=True)
            (repo / rel).write_bytes(fake_bytes(source, name))
    descriptor = {"schema": 1, "source": source, "qualification": q, "stamps": stamps}
    descriptor_path = evidence / "descriptor.json"
    pin = write_json(descriptor_path, descriptor)["sha256"]
    if scenario == "qualification-absent":
        (evidence / "qualification.json").unlink()
    if scenario == "stamp-absent":
        Path(next(iter(stamps.values()))["path"]).unlink()
    if scenario == "pin-mismatch":
        pin = "0" * 64
    if scenario == "dirty":
        with (repo / "Jarvis.slnx").open("ab") as f:
            f.write(b"\n<!-- dirty before publish -->\n")
    # Existing downloads must survive rejected batches byte-for-byte, and
    # successful publication must preserve unrelated inventory/checksum behavior.
    downloads = repo / "artifacts/downloads"
    downloads.mkdir(parents=True, exist_ok=True)
    old_name = "unrelated.bin"
    (downloads / old_name).write_bytes(b"old unrelated download")
    old_line = digest(downloads / old_name) + "  " + old_name
    (downloads / "SHA256SUMS").write_text(old_line + "\n")
    original = {p.name: p.read_bytes() for p in downloads.iterdir()}
    shim = root / "bin"
    shim.mkdir()
    shim_path = shim / "dotnet"
    shim_path.write_text("#!/bin/sh\nexec " + shlex.quote(sys.executable) + " -B " +
                         shlex.quote(str(SCRIPT)) + ' --fake-dotnet "$@"\n')
    shim_path.chmod(0o700)
    child_env = dict(env, PATH=str(shim) + os.pathsep + env["PATH"],
                     COMPOSED_FIXTURE_ROOT=str(root), COMPOSED_FIXTURE_SOURCE=json.dumps(source),
                     COMPOSED_FIXTURE_SCENARIO=scenario)
    publisher = ("scripts/publish-client-downloads.cs" if product == "JarvisClient"
                 else "scripts/publish-server-download.cs")
    argv = [str(real_dotnet), "run", "--file", publisher, "-c", "Release",
            "-p:PublishAot=false", "-p:SelfContained=false", "--disable-build-servers", "--"]
    if scenario != "missing-arguments":
        argv += ["--evidence", str(descriptor_path), "--evidence-sha256", pin]
    p = run(argv, repo, child_env, timeout=120)
    inv = root / "invocations.jsonl"
    count = len(inv.read_text().splitlines()) if inv.exists() else 0
    stdout = p.stdout.decode(errors="replace")
    stderr = p.stderr.decode(errors="replace")
    expected_code = 0 if scenario == "positive" else (7 if scenario in
                    ("build-failed", "late-client-failure") else 2)
    need(p.returncode == expected_code,
         f"{scenario}: expected {expected_code}, got {p.returncode}\n{stdout}\n{stderr}")
    expected_invocations = (len(rids) if scenario == "positive" else
                            2 if scenario == "late-client-failure" else
                            1 if scenario in ("stale", "substitution", "build-failed",
                                              "dirty-after-build") else 0)
    need(count == expected_invocations, f"{scenario}: wrong invocation count {count}")
    if scenario != "positive":
        need({p.name: p.read_bytes() for p in downloads.iterdir()} == original,
             f"{scenario}: rejected batch changed final downloads")
    else:
        expected_names = {names(target_for(product, rid))[1] for rid in rids}
        need(set(p.name for p in downloads.iterdir()) == expected_names | set(original),
             "wrong final inventory")
        lines = (downloads / "SHA256SUMS").read_text().splitlines()
        need(set(lines) == {old_line} | {digest(downloads / name) + "  " + name
                                         for name in expected_names}, "checksum mismatch")
        if product == "JarvisClient":
            need(all((downloads / n).stat().st_mode & 0o777 == 0o755 for n in expected_names
                     if not n.endswith(".exe")), "Unix executable permission not preserved")
        session, = (repo / "artifacts/release-provenance").glob("publisher-*")
        report = json.loads((session / "manifest.json.report.json").read_text())
        manifest = json.loads((session / "manifest.json").read_text())
        need(report["status"] == "verified" and report["source"]["commit"] == source["commit"]
             and report["source"]["tree"] == source["tree"]
             and {x["id"] for x in report["artifacts"]} == expected_names, "wrong final verification")
        for entry in manifest["artifacts"]:
            path = Path(entry["path"])
            release = json.loads(Path(entry["release"]["path"]).read_text())
            build = json.loads(Path(entry["build"]["path"]).read_text())
            need(path.parent == downloads and entry["expectedSha256"] == digest(path)
                 == entry["observedSha256"] == build["artifactSha256"] == release["artifactSha256"]
                 and release["qualificationReceiptSha256"] == q["sha256"]
                 and release["callerEvidenceSha256"] == pin and release["buildId"] == build["buildId"]
                 and release["buildReceiptSha256"] == digest(Path(entry["build"]["path"])),
                 "final receipt linkage incorrect")
    print(json.dumps({"product": product, "scenario": scenario, "publisherExit": p.returncode,
                      "buildInvocations": count, "fixtureOnly": True}), flush=True)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--fake-dotnet":
        return fake_dotnet(sys.argv[2:])
    need(len(sys.argv) == 1 and sys.platform.startswith("linux"), "Linux disposable fixture only")
    origin = SCRIPT.parents[2]
    real_dotnet = Path(shutil.which("dotnet") or "").resolve()
    need(real_dotnet.is_file(), "real SDK unavailable")
    with tempfile.TemporaryDirectory(prefix="jarvis-composed-publisher-") as tmp:
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
        env["COMPOSED_FIXTURE_SDK"] = output([real_dotnet, "--version"], root, env).decode().strip()
        commit, tree = git(origin, env, "rev-parse", "HEAD", "HEAD^{tree}").decode().splitlines()
        source = {"commit": commit, "tree": tree}
        need(observation(origin, env, "before-build")["status"] == "clean", "candidate must be frozen and clean")
        print(json.dumps({"source": source, "fixtureOnly": True, "sdk": env["COMPOSED_FIXTURE_SDK"]}), flush=True)
        for scenario in ("positive", "missing-arguments", "qualification-absent", "qualification-stale",
                         "qualification-unknown", "stamp-absent", "malformed-stamp", "pin-mismatch",
                         "dirty", "stale", "substitution", "build-failed", "dirty-after-build"):
            case(root / scenario, origin, source, real_dotnet, env, "JarvisServer", scenario)
        for scenario in ("positive", "late-client-failure"):
            case(root / ("client-" + scenario), origin, source, real_dotnet, env, "JarvisClient", scenario)
    print("PASS: 15 actual publisher controls; synthetic evidence only", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
