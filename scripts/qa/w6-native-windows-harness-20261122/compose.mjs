#!/usr/bin/env node
// Default OFF. --check reads immutable Git objects only. --stage is guarded,
// native-Windows-only and writes to a preattested disposable account/VM root.
// There is deliberately NO --run, restore, build, vstest, or Client.Tests path.
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import { pins, nativeMatrix } from "./source-pins.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const die = (why) => { throw new Error("W6_HOLD: " + why); };
const parse = (args) => {
  const [mode = "--check", ...rest] = args;
  if (!["--check", "--stage"].includes(mode)) die("Only --check and --stage are supported; native execution is not implemented.");
  const options = {};
  for (let i = 0; i < rest.length; i += 2) {
    if (!rest[i]?.startsWith("--") || rest[i + 1] === undefined) die("Missing or malformed option");
    const k = rest[i].slice(2);
    if (!["repo", "scratch-parent", "dedicated-sid", "operator-sid", "review-commit", "operator-denial-receipt"].includes(k) || k in options) die("Unknown/duplicate option " + k);
    options[k] = rest[i + 1];
  }
  return { mode, options };
};
const run = (exe, args, cwd, options = {}) => {
  const env = { ...process.env };
  // Git config and worktree/index/author overrides must NEVER come from the operator.
  for (const key of Object.keys(env)) if (key.toUpperCase().startsWith("GIT_")) delete env[key];
  env.GIT_CONFIG_NOSYSTEM = "1";
  env.GIT_CONFIG_GLOBAL = os.platform() === "win32" ? "NUL" : "/dev/null";
  const result = spawnSync(exe, args, {
    cwd, env, encoding: options.encoding ?? "utf8", input: options.input,
    maxBuffer: 200 * 1024 * 1024, timeout: 120000, windowsHide: true
  });
  if (result.error || result.status !== 0 || result.signal)
    die(exe + " exited nonzero, timed out, or could not start: " +
        (result.error?.message || String(result.stderr || "").slice(-1000)));
  return result.stdout;
};
const git = (repo, ...args) => run("git", ["-C", repo, ...args], repo).trim();
export function assertEqual(actual, expected, description) {
  if (actual !== expected) die(description + " mismatch");
}
export function requirePinStructure(manifest) {
  if (!/^[0-9a-f]{40}$/.test(manifest.w6.commit) || !/^[0-9a-f]{40}$/.test(manifest.wq11.commit)) die("Invalid immutable SHA pins.");
  if (!Array.isArray(nativeMatrix) || nativeMatrix.length < 9) die("Native acceptance matrix incomplete.");
  if (!nativeMatrix.some(([label]) => /60-second/.test(label))) die("Missing real default wait case.");
  for (const source of [manifest.w6, manifest.wq11]) {
    for (const [name, hash] of Object.entries(source.paths)) {
      if (name.includes("..") || name.startsWith("/") || !/^[0-9a-f]{40}$/.test(hash)) die("Unsafe pinned path/object.");
    }
  }
}
const pinnedBlob = (repo, source, filename) => {
  const output = git(repo, "ls-tree", source.commit, "--", filename);
  const match = /^100644 blob ([0-9a-f]{40})\t([^\r\n]+)$/.exec(output);
  if (!match || match[2] !== filename) die("Missing regular tracked pinned source " + filename);
  assertEqual(match[1], source.paths[filename], "Pinned Git blob " + filename);
};
function verifySources(repo) {
  requirePinStructure(pins);
  for (const src of [pins.w6, pins.wq11]) {
    assertEqual(git(repo, "cat-file", "-t", src.commit), "commit", "Git source commit");
    for (const filename of Object.keys(src.paths)) pinnedBlob(repo, src, filename);
  }
  const w6LinuxOnly = git(repo, "show", pins.w6.commit + ":Client.Tests/HermeticHarnessTests.fs");
  if (!w6LinuxOnly.includes("OperatingSystem.IsLinux()") || !w6LinuxOnly.includes("failwith")) die("W6 Windows safety blocker changed.");
  const wq11Guard = git(repo, "show", pins.wq11.commit + ":Client.Tests/HermeticWindowsGuard.fs");
  for (const text of ["GetUserProfileDirectory", "SHGetKnownFolderPath", "GetFinalPathNameByHandle", "SetInformationJobObject", "assertOwnerOnly", "attestScratchParent"])
    if (!wq11Guard.includes(text)) die("WQ11 safety primitive absent: " + text);
  const wq11Runner = git(repo, "show", pins.wq11.commit + ":scripts/test-client-hermetic.fsx");
  for (const text of ["createKillOnCloseJob", "attestScratchParent", "waitForJobEmpty", "Client.Tests.dll"])
    if (!wq11Runner.includes(text)) die("WQ11 launcher primitive absent: " + text);
  return { w6: pins.w6.commit, wq11: pins.wq11.commit, originalProductionBlob: pins.w6.paths["Client/Modules/OrchestrationApi.fs"],
           originalReviewTestsBlob: pins.w6.paths["Client.Tests/ReviewWorkflowTests.fs"] };
}
export function isolatedChildPath(parent, child) {
  const a = path.resolve(parent), b = path.resolve(child);
  if (b === a || !b.startsWith(a + path.sep)) die("Source or stage must be strictly inside preattested scratch parent.");
  return b;
}
function noLinkedPath(root, candidate) {
  isolatedChildPath(root, candidate);
  let current = path.resolve(candidate);
  const stop = path.resolve(root);
  while (current !== stop) {
    const st = fs.lstatSync(current);
    if (st.isSymbolicLink()) die("Reparse/symlink component in disposable source path.");
    current = path.dirname(current);
  }
}
function nativeAttestation(opts) {
  if (process.platform !== "win32") die("Linux/macOS can perform static and negative checks only.");
  for (const key of ["repo", "scratch-parent", "dedicated-sid", "operator-sid", "review-commit", "operator-denial-receipt"])
    if (!opts[key]) die("Missing native preflight prerequisite " + key);
  if (!path.isAbsolute(opts["scratch-parent"]) || !path.isAbsolute(opts.repo)) die("Only absolute OS paths.");
  if (!/^[0-9a-f]{40}$/.test(opts["review-commit"])) die("Exact independently reviewed harness SHA is required.");
  if (!/^S-1-/.test(opts["dedicated-sid"]) || !/^S-1-/.test(opts["operator-sid"]) || opts["dedicated-sid"] === opts["operator-sid"]) die("Independent dedicated SID required.");
  // FSI is the read-only, native-OS-token preflight process. No scratch creation,
  // Git restore, package acquisition or testhost launch occurs before this call.
  const args = ["fsi", path.join(here, "native-preflight.fsx"), "--",
    "--phase", "parent", "--scratch-parent", opts["scratch-parent"],
    "--dedicated-sid", opts["dedicated-sid"], "--operator-sid", opts["operator-sid"]];
  const out = run("dotnet", args, here);
  if (!/^W6_PREFLIGHT_OK phase=parent /m.test(out)) die("No native parent attestation.");
  // An operator-profile denial cannot safely be proved by probing the live
  // operator's files. Its independent attestation is a required HOLD in this
  // source-only phase, never silently inferred from TEMP/USERPROFILE.
  return out;
}

function stage(opts) {
  nativeAttestation(opts);
  const parent = fs.realpathSync(opts["scratch-parent"]);
  const repo = fs.realpathSync(opts.repo);
  noLinkedPath(parent, repo);
  noLinkedPath(parent, here);
  const receiptPath = fs.realpathSync(opts["operator-denial-receipt"]);
  noLinkedPath(parent, receiptPath);
  const receipt = JSON.parse(fs.readFileSync(receiptPath, "utf8"));
  if (receipt.schema !== 1 || receipt.dedicatedSid !== opts["dedicated-sid"] ||
      receipt.operatorSid !== opts["operator-sid"] ||
      receipt.sourceCommit !== pins.w6.commit ||
      receipt.reviewedHarnessCommit !== opts["review-commit"] ||
      receipt.operatorProfileDeniedByOs !== true ||
      receipt.approvedByLead !== true) die("Independent operator-profile denial / Lead receipt unavailable.");
  assertEqual(git(repo, "rev-parse", "HEAD"), pins.w6.commit, "Disposable source HEAD");
  if (git(repo, "status", "--porcelain") !== "") die("Disposable W6 input checkout dirty.");
  assertEqual(git(here, "rev-parse", "HEAD"), opts["review-commit"], "Reviewed harness HEAD");
  if (git(here, "status", "--porcelain") !== "") die("Harness source checkout dirty.");
  verifySources(repo);
  const modes = git(repo, "ls-tree", "-r", pins.w6.commit);
  if (modes.split(/\r?\n/).some(line => /^(120000|160000) /.test(line)))
    die("W6 tracked symlink or gitlink: refuse automatic extraction.");
  // The first filesystem allocation is AFTER native token/known-folder/
  // physical GUID, ACL, source and independent Lead receipt validation.
  const root = fs.mkdtempSync(path.join(parent, "w6-source-only-"));
  const archive = spawnSync("git", ["-C", repo, "archive", "--format=tar", pins.w6.commit], {
    cwd: repo, encoding: null, maxBuffer: 200 * 1024 * 1024, timeout: 120000, windowsHide: true
  });
  if (archive.status !== 0 || archive.error || !archive.stdout?.length)
    die("Source archive failed; do not automatically delete unsafe staged root.");
  const unpack = spawnSync("tar", ["-xf", "-", "-C", root], {
    cwd: parent, input: archive.stdout, encoding: "utf8", maxBuffer: 1024 * 1024,
    timeout: 120000, windowsHide: true
  });
  if (unpack.status !== 0 || unpack.error)
    die("Pinned source extraction failed; quarantined root requires manual disposal.");
  const preserved = ["Client/Modules/OrchestrationApi.fs", "Client.Tests/ReviewWorkflowTests.fs"];
  for (const name of preserved)
    assertEqual(run("git", ["hash-object", path.join(root, name)], repo).trim(),
      pins.w6.paths[name], "Original W6 blob before overlay " + name);
  const wq11Blob = (name) => {
    const r = spawnSync("git", ["-C", repo, "show", pins.wq11.commit + ":" + name], {
      cwd: repo, encoding: null, maxBuffer: 20 * 1024 * 1024, timeout: 30000, windowsHide: true
    });
    if (r.status !== 0 || r.error) die("Missing WQ11 blob " + name);
    return r.stdout;
  };
  for (const name of ["Client.Tests/HermeticWindowsGuard.fs",
                      "Client.Tests/HermeticHarnessTests.fs", "scripts/test-client-hermetic.fsx"])
    fs.writeFileSync(path.join(root, name), wq11Blob(name));
  const projectPath = path.join(root, "Client.Tests", "Client.Tests.fsproj");
  let project = fs.readFileSync(projectPath, "utf8");
  const compile = '<Compile Include="HermeticHarnessTests.fs"/>';
  if (project.split(compile).length !== 2 ||
      project.includes('Include="HermeticWindowsGuard.fs"')) die("Unexpected fsproj overlay seam.");
  project = project.replace(compile, '<Compile Include="HermeticWindowsGuard.fs"/>\n' +
    '        <Compile Include="W6NativeChildGuard.fs"/>\n        ' + compile);
  fs.writeFileSync(projectPath, project);
  const fixturePath = path.join(root, "Client.Tests", "HermeticHarnessTests.fs");
  let fixture = fs.readFileSync(fixturePath, "utf8");
  const anchor = "member this.BeforeAll() =";
  if (fixture.split(anchor).length !== 2) die("Unexpected reviewed WQ11 fixture seam.");
  fixture = fixture.replace(anchor, anchor +
    "\n        if OperatingSystem.IsWindows() then W6NativeChildGuard.attest ()");
  fs.writeFileSync(fixturePath, fixture);
  fs.copyFileSync(path.join(here, "W6NativeChildGuard.fs"),
                  path.join(root, "Client.Tests", "W6NativeChildGuard.fs"));
  const preflightPath = path.join(root, "scripts", "qa", "w6-native-windows-harness-20261122");
  fs.mkdirSync(preflightPath, { recursive: true });
  fs.copyFileSync(path.join(here, "native-preflight.fsx"),
                  path.join(preflightPath, "native-preflight.fsx"));
  for (const name of preserved)
    assertEqual(run("git", ["hash-object", path.join(root, name)], repo).trim(),
      pins.w6.paths[name], "Original W6 blob after overlay " + name);
  fs.writeFileSync(path.join(root, "W6-STAGED-SOURCE-ONLY.json"), JSON.stringify({
    status: "PREPARED_NOT_EXECUTED", w6: pins.w6, wq11: pins.wq11,
    reviewedHarnessCommit: opts["review-commit"], unchangedReviewedBlobs: preserved,
    substitutions: ["Client.Tests/HermeticWindowsGuard.fs", "Client.Tests/HermeticHarnessTests.fs",
      "Client.Tests/W6NativeChildGuard.fs", "Client.Tests/Client.Tests.fsproj",
      "scripts/test-client-hermetic.fsx"],
    nativeTestsExecuted: 0,
    unresolved: ["no Windows Release terminal TRX", "real unmodified 60-second wait test absent",
      "operator ACL denial receipt externally authenticated by Lead only", "independent frozen source review"]
  }, null, 2));
  console.log(JSON.stringify({ status: "PREPARED_NOT_EXECUTED", root, nativeTestsExecuted: 0 }));
}

function main() {
  const { mode, options } = parse(process.argv.slice(2));
  if (mode === "--stage") { stage(options); return; }
  const repo = path.resolve(options.repo || process.cwd());
  const result = verifySources(repo);
  console.log(JSON.stringify({status:"STATIC_ONLY", noWindowsExecution:true, noOverlayWrites:true, source:result,
    matrixCases:nativeMatrix.length, nativeHold:"dedicated OS token/ACL denial, reviewed overlay and native Release TRX absent"}));
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 42; }
}
