# Independent review — Reviewer 1

**Scope:** Adjudicator3 NARROW repair of Reviewer4's disconnected-Beta no-fallback observability finding only. This is **not** acceptance of the complete named-setting tool, W24 integration, full Client/Server suite, native deployment, or cutover.

**Frozen source:** `f61b5b3ec75bb2a91d4dab99c64f4a96d23311ec` (parent `da4e54cf0205f67fb4d5a5716b888829f979fc2c`), private clean registered `test/client-named-setting-two-native-w25-20261122`. Independent `git diff-tree` and parent diff show exactly one changed path: `Client.Tests/NamedSettingTwoNative/TwoNativeNamedSettingTests.fs`. `git rev-parse HEAD` matches the pinned review head; worktree is clean.

**Code review:** The test now instruments Alpha's `IHubClients.Client(connA)` lookups and Alpha recipient `ReceiveCommand` dispatches. It snapshots both counters *after* disconnecting the Beta transport and before issuing exact-device Beta Set, then requires a zero delta for each. The surrounding existing test exercises a successful Beta Set with Beta's own revision and on-disk readback, verifies both clients' untouched config bytes and revisions after offline Beta refusal, and confirms Alpha remains connected. The new assertions reject wrong-target fallback even when Alpha CAS would return an error and the config bytes remain unchanged. No production or project file changed in this narrow commit.

**Actual terminal evidence (inspected, not rerun):**
- Deliberately wrong-Alpha-lookup mutant: managed job `f9fa9e9143764aac8c14fcdbd921f10d`, SDK 11.0.100 Release, **exit 1**, focused TRX **0 passed / 1 failed**; precise failure `Disconnected Beta must not trigger any Alpha hub.Client lookup`, expected 0, observed 1. Raw TRX SHA-256 `59b654e737011ee455b2093bb05e6eba08144bf9d4f3a8c9c31aa32a749c6e13`.
- Clean frozen source: managed job `b18f6a32b3754102aaf97fd22c9ee2e5`, SDK 11.0.100 Release, **exit 0**, focused TRX **1 passed / 0 failed / 0 skipped**. Independently read raw TRX Counters and verified SHA-256 `4616755d165a7565b8b35e0975b8d9eaa72ba5e9c4323fd166b8ef21a71575dd`.

**Evidence boundaries:** This is a one-test isolated typed-harness/in-process hub-double proof with two disposable native child processes; registration is simulated. No new independent test run was made, and the older full Client attempts `03ac620c`/`101f470e` remain UNKNOWN, not PASS. No full Server Release, final product E2E, main integration, deploy, live sends, or B2/cutover is approved by this narrow verdict.

**Verdict:** **Accepted** for the independently scoped one-file NARROW repair. The added lookup+dispatch zero-delta controls satisfy the adjudicated no-fallback test requirement; mutant falsification and clean terminal evidence are both explicit. Full product/release gates remain HOLD.
