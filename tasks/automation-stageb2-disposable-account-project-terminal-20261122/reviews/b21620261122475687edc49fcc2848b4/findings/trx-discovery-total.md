# P2 — Exact TRX total is misstated (documentation/evidence only)

**Frozen source:** `475607edc49fcc2848b4e4c9e45a2696464e9aa2`, direct parent `d312f1cc83685e781ce69e57ad0d4bf613bfb843`.

**Original requirement:** The bounded task explicitly requires managed .NET 11 Release commands, terminal exits and **exact TRX totals**, distinguishing an intentionally RED B2 contract from passed disposable-only tests.

**Reproduction:** The author’s source-bound job `2ac75d9ec9244ebdbf4fd181d860e05b` exited 0, with `w16-b2-native-account-and-b1.trx` counters `total=23 executed=22 passed=22 failed=0`. Independent frozen-commit Release job `a449573a37194a3690d2d58685b0c612` also exited 0, 22 executed/passed; `reviewer2-frozen-475607ed.trx` separately records `total=23 executed=22 passed=22 failed=0`. NUnit prints `Skipped StartedAccountCandidateMustBridgeToB1WithoutRewritingCanonicalLease` because it is marked `Explicit`; the console’s summary reports 22 run and skipped 0.

**Defect:** `docs/testing/automation-stageb2-disposable-20261122.md`, final source-bound terminal section (line 59), says “TRX: 22 passed / 0 failed / 0 skipped, total 22”. It conflates the runner’s **executed** total with the XML TRX’s **discovered** total. The current review summary also says “TRX 22/22.” That is not the exact XML count.

**Impact:** Evidence consumers may incorrectly conclude that the retained intentional B2 RED is absent from discovery, rather than excluded from the green execution. It also fails the task’s exact-TRX-evidence obligation. This does **not** indicate a failing green test, production activation, or invented native B2 success.

**Bounded remedy:** Correct the documentation to state **23 discovered, 22 executed/passed, 0 executed failures, 1 explicitly excluded intentional RED**; keep the B2 RED separate at `c3a5cae01a964da49b432b5bc427e5c9` exit 1 / 1 failed. No production code, existing tests, new authority or additional tasks required. Independent adjudication of any further work remains outside this review.