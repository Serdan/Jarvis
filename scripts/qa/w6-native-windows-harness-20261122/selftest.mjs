import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import { pins, nativeMatrix } from "./source-pins.mjs";
import { assertEqual, requirePinStructure, isolatedChildPath } from "./compose.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "../../..");
const cli = path.join(here, "compose.mjs");
const invoke = (...args) => spawnSync(process.execPath, [cli, ...args], {
  cwd: root, encoding: "utf8", timeout: 30000, maxBuffer: 300000
});

test("immutable exact SHA source pins and matrix are well formed", () => {
  requirePinStructure(pins);
  assert.equal(nativeMatrix.length, 9);
  assert.ok(nativeMatrix.some(([name]) => /unmodified default 60-second/.test(name)));
});
test("malformed source pin and path traversal rejected (negative)", () => {
  assert.throws(() => requirePinStructure({ ...pins, w6: {
    ...pins.w6, commit: "dirty-QA", paths: pins.w6.paths
  }}), /HOLD/);
  assert.throws(() => requirePinStructure({ ...pins, w6: {
    ...pins.w6, paths: { "../operator-profile": "a".repeat(40) }
  }}), /HOLD/);
  assert.throws(() => assertEqual("dirty", pins.w6.commit, "pin"), /HOLD/);
});
test("scratch root prefix confusion, parent, escape denied", () => {
  assert.throws(() => isolatedChildPath("/isolation/w6", "/isolation/w6"), /HOLD/);
  assert.throws(() => isolatedChildPath("/isolation/w6", "/isolation/w6-alt/escape"), /HOLD/);
  assert.throws(() => isolatedChildPath("/isolation/w6", "/isolation/real-user"), /HOLD/);
  assert.equal(isolatedChildPath("/isolation/w6", "/isolation/w6/fixture"), "/isolation/w6/fixture");
});
test("static source pin check is read-only and explicit non-Windows acceptance", () => {
  const before = spawnSync("git", ["status", "--porcelain"], {cwd:root, encoding:"utf8"}).stdout;
  const r = invoke("--check", "--repo", root);
  assert.equal(r.status, 0, r.stderr);
  const result = JSON.parse(r.stdout.trim());
  assert.equal(result.status, "STATIC_ONLY");
  assert.equal(result.noWindowsExecution, true);
  assert.equal(result.noOverlayWrites, true);
  assert.equal(result.source.w6, pins.w6.commit);
  assert.equal(result.source.originalProductionBlob, pins.w6.paths["Client/Modules/OrchestrationApi.fs"]);
  assert.equal(result.matrixCases, 9);
  const after = spawnSync("git", ["status", "--porcelain"], {cwd:root, encoding:"utf8"}).stdout;
  assert.equal(after, before);
});
test("Linux --stage denied before creation, even with forged identity flags", () => {
  if (process.platform === "win32") return;
  for (const flags of [[], ["--repo", root, "--scratch-parent", "/tmp", "--dedicated-sid",
    "S-1-5-21-2", "--operator-sid", "S-1-5-21-1", "--review-commit", "a".repeat(40),
    "--operator-denial-receipt", "/tmp/forged-receipt.json"]]) {
    const r = invoke("--stage", ...flags);
    assert.equal(r.status, 42);
    assert.match(r.stderr, /W6_HOLD: Linux\/macOS/);
  }
});
test("unsafe --run and unknown passthrough switches are impossible", () => {
  for (const flags of [["--run"],["--stage","--test","Client.Tests"]]) {
    const r = invoke(...flags);
    assert.equal(r.status, 42);
    assert.match(r.stderr, /W6_HOLD/);
  }
});
test("reviewed Windows safety code remains authored but not executed", () => {
  const guard = fs.readFileSync(path.join(here, "native-preflight.fsx"), "utf8");
  const child = fs.readFileSync(path.join(here, "W6NativeChildGuard.fs"), "utf8");
  for (const requirement of ["OpenProcessToken", "GetUserProfileDirectory", "SHGetKnownFolderPath",
    "GetFinalPathNameByHandle", "WindowsBuiltInRole.Administrator", "Desktop", "Documents",
    "RoamingAppData", "LocalAppData", "AreAccessRulesProtected", "FileAttributes.ReparsePoint"])
    assert.ok(guard.includes(requirement), requirement);
  for (const requirement of ["W6_DEDICATED_SID", "W6_OPERATOR_SID", "attestScratchParent",
    "assertOwnerOnly", "W6_PREFLIGHT_OK phase=testhost"])
    assert.ok(child.includes(requirement), requirement);
});
