#!/usr/bin/env python3
"""Read back frozen synthetic fixture evidence; not independent attestation."""
import base64
import hashlib
import json
from pathlib import Path

from fixture import SOURCE, HELPER, CLIENT_RIDS, target_for, names, occurrence, need, digest

ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "docs/reviews/release-provenance-publish-preflight-20261127"
EXPECTED = [
    ("JarvisServer", "positive", 0, 0, True, True, "accepted"),
    ("JarvisClient", "positive", 0, 0, True, True, "accepted"),
    ("JarvisServer", "absent", 0, 2, False, True, "rejected"),
    ("JarvisServer", "qualification-absent", 0, 0, True, False, "rejected"),
    ("JarvisServer", "dirty", 0, 1, True, False, "rejected"),
    ("JarvisServer", "stale", 0, 0, False, True, "rejected"),
    ("JarvisServer", "substitution", 0, 1, True, True, "rejected"),
    ("JarvisServer", "build-failed", 7, None, None, None, "rejected"),
]

def hash_bytes(raw):
    return hashlib.sha256(raw).hexdigest()

def verify_case(summary, bundle, expected, positive_build_pin):
    product, case, publisher_exit, helper_exit, fresh, qual, gate = expected
    need((summary["product"], summary["scenario"], summary["publisherExit"],
          summary.get("helperExit"), summary.get("freshOccurrence"),
          summary.get("qualificationVerified"), summary["gate"]) == expected,
         f"wrong expected control outcome: {case}")
    need(bundle["receiptBundle"] == case, "bundle scenario mismatch")
    files, downloads = bundle["files"], bundle["downloads"]
    receipts = {}
    for name, entry in files.items():
        raw = entry["rawUtf8"].encode()
        need(hash_bytes(raw) == entry["sha256"], f"receipt digest mismatch: {case}/{name}")
        receipts[name] = json.loads(raw)
    copied = {}
    for name, entry in downloads.items():
        raw = base64.b64decode(entry["rawBase64"], validate=True)
        need(hash_bytes(raw) == entry["sha256"], f"copy digest mismatch: {case}/{name}")
        copied[name] = raw
    publisher = receipts["publisher.json"]
    need(files["publisher.json"]["sha256"] == summary["publisherReceipt"], "publisher receipt not pinned")
    publisher_path = ("scripts/publish-client-downloads.cs" if product == "JarvisClient"
                      else "scripts/publish-server-download.cs")
    need(publisher["argv"][1:] == ["run", "--file", publisher_path, "-c", "Release",
                                  "-p:PublishAot=false", "-p:SelfContained=false", "--disable-build-servers"]
         and publisher["source"] == SOURCE and publisher["state"] == "exited"
         and publisher["exitCode"] == publisher_exit and publisher["fixtureOnly"] is True,
         "unexpected publisher terminal receipt")
    invocations = [v for k, v in receipts.items() if k.endswith(".invocation.json")]
    need(len(invocations) == summary["invocations"], "invocation count mismatch")
    if case == "build-failed":
        need(len(invocations) == 1 and invocations[0]["exitCode"] == 7
             and invocations[0]["state"] == "exited" and not downloads and "manifest.json" not in receipts,
             "failed build unexpectedly produced release")
        return positive_build_pin

    nonce = summary["nonce"]
    need(publisher["nonce"] == nonce, "publisher occurrence mismatch")
    if qual:
        q = receipts["qualification.json"]
        need(files["qualification.json"]["sha256"] == summary["qualification"]
             and q["source"] == SOURCE and q["nonce"] == nonce and q["state"] == "exited"
             and q["exitCode"] == 0 and q["fixtureOnly"] is True
             and all(q[p]["source"] == SOURCE and q[p]["status"] == "clean"
                     for p in ("before", "after")), "qualification receipt mismatch")
    elif case == "qualification-absent":
        need("qualification.json" not in receipts, "missing qualification control not missing")
    else:
        need(receipts["qualification.json"]["before"]["status"] == "dirty",
             "dirty qualification control not dirty")

    manifest = receipts["manifest.json"]
    report = receipts["verifier-report.json"]
    need(files["manifest.json"]["sha256"] == summary["manifest"] == report["manifestSha256"]
         and manifest["source"] == SOURCE and report["status"] == summary["helperStatus"]
         and report["source"]["commit"] == SOURCE["commit"] and report["source"]["tree"] == SOURCE["tree"],
         "manifest or verifier report identity mismatch")
    targets = [target_for(product, rid) for rid in
               (CLIENT_RIDS if product == "JarvisClient" else ("linux-x64",))]
    expected_names = {names(t)[1] for t in targets}
    checksums = {}
    for line in copied["SHA256SUMS"].decode().splitlines():
        sha, name = line.split("  ", 1)
        need(name not in checksums, "duplicate checksum entry")
        checksums[name] = sha
    need(set(checksums) == expected_names and set(downloads) == expected_names | {"SHA256SUMS"},
         "unexpected batch membership")
    entries = {a["id"]: a for a in manifest["artifacts"]}
    proofs = {a["name"]: a for a in summary["artifacts"]}
    need(set(entries) == set(proofs) == expected_names and len(entries) == len(manifest["artifacts"]),
         "artifact inventory mismatch")
    for target in targets:
        _, name = names(target)
        a, proof = entries[name], proofs[name]
        inv = receipts[name + ".invocation.json"]
        expected_args = (["scripts/build.cs", "publish-client", "--rid", target["rid"],
                          "--server", "https://jarvis2.kehlet.dev/client"] if product == "JarvisClient"
                         else ["scripts/build.cs", "publish-server", "--rid", "linux-x64"])
        need(inv["argv"] == expected_args and inv["nonce"] == nonce and inv["state"] == "exited"
             and inv["exitCode"] == 0 and files[name + ".invocation.json"]["sha256"] == proof["invocation"],
             "nested invocation receipt mismatch")
        need(a["target"] == target and Path(a["path"]).name == name
             and a["expectedSha256"] == proof["expected"] == checksums[name]
             and a["observedSha256"] == proof["observed"] == downloads[name]["sha256"],
             "artifact bytes not bound to copied file and checksum")
        need((a["observedSha256"] != a["expectedSha256"]) == (case == "substitution"),
             "unexpected checksum comparison")
        for kind in ("build", "stamp", "release"):
            if case == "absent":
                need(kind not in a and kind not in proof["receiptPins"], "absent receipt unexpectedly present")
            else:
                key = name + "." + kind + ".json"
                need(Path(a[kind]["path"]).name == key
                     and a[kind]["sha256"] == proof["receiptPins"][kind] == files[key]["sha256"],
                     "receipt pin mismatch")
        if case == "absent":
            continue
        build, release = receipts[name + ".build.json"], receipts[name + ".release.json"]
        need(build["fixtureOnly"] is True and release["fixtureOnly"] is True
             and build["source"] == release["source"] == SOURCE
             and build["target"] == release["target"] == target
             and build["buildId"] == release["buildId"]
             and release["buildReceiptSha256"] == a["build"]["sha256"]
             and release["publisherReceiptSha256"] == summary["publisherReceipt"]
             and release["qualificationReceiptSha256"] == summary["qualification"]
             and release["artifactSha256"] == a["observedSha256"]
             and build["artifactSha256"] == a["expectedSha256"],
             "release/build receipt linkage mismatch")
        if case == "stale":
            need(build["buildId"] != occurrence(nonce, target)
                 and a["build"]["sha256"] == positive_build_pin and "receiptPins" not in inv,
                 "stale control did not reuse prior occurrence")
        else:
            need(build["buildId"] == occurrence(nonce, target)
                 and inv["receiptPins"] == {k: a[k]["sha256"] for k in ("build", "stamp")},
                 "fresh receipt not bound at build boundary")
        if case == "positive" and product == "JarvisServer":
            positive_build_pin = a["build"]["sha256"]
    return positive_build_pin

def main():
    job = json.loads((EVIDENCE / "final-managed-run.json").read_text())
    need(job["JobId"] == "deda0527a5f245a7908d721ea338fe93"
         and job["Status"] == {"case": "Completed", "exitCode": 0}
         and job["Truncated"] is False and job["NextSequence"] == 18, "job not terminal or incomplete")
    events = job["Events"]
    need([e["Sequence"] for e in events] == list(range(1, 19)), "missing job output sequence")
    header = json.loads(events[0]["Text"])
    need(header["source"] == SOURCE and header["helperCommit"] == HELPER
         and header["helperSha256"] == "905a0edac953f4495624ca52e743bb688af1bd79fa0a0b3f32e2721aaf30c7eb"
         and header["harnessSha256"] == digest(Path(__file__).with_name("fixture.py"))
         and header["fixtureOnly"] is True, "wrong source or fixture identity")
    prior = None
    for i, expected in enumerate(EXPECTED):
        summary = json.loads(events[1 + i * 2]["Text"])
        pointer = events[2 + i * 2]
        filename = expected[0] + "-" + expected[1] + ".json"
        need(pointer["Bundle"] == filename, "wrong bundle pointer")
        original = json.loads((EVIDENCE / filename).read_text())
        need(original["Sequence"] == pointer["Sequence"] and original["Stream"] == pointer["Stream"],
             "bundle sequence mismatch")
        prior = verify_case(summary, json.loads(original["Text"]), expected, prior)
        print(f"{expected[0]} {expected[1]}: frozen evidence consistent")
    need(events[-1]["Text"].strip() == "PASS: actual publisher command boundary, synthetic evidence only",
         "missing fixture completion marker")
    print("PASS: 8 frozen synthetic evidence cases; not attestation or production qualification")

if __name__ == "__main__":
    main()
