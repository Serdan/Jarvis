# P1 BLOCKING — Direct zero-dependency manual requeue commits through a corrupt owner reverse index

**Reviewed frozen production SHA:** `48f1a8c6030677960a3d84629c4e29cecf56a132`. **Primary code:** `Client/Modules/ChallengeWorkflow.fs:1810-1842`, especially `1824-1830` (verification skipped when `wait.ChallengeIds = []`); `Client/Modules/ReviewWorkflow.fs:953-961` (owner enumeration verifies corrupted index on inbox path only).

## Independent executable proof

The separately unreviewed Worker 7 expanded 17-mode matrix at `332020cd50f3a2a3986933763edb8f81251e0b1c` includes mode 15 but reads owner inbox **after** injecting a forged owner index, causing an early `LedgerIntegrityError` before its intended manual-requeue assertion. That original failure was a **fixture sequencing artifact**, not proof that direct requeue itself was safe.

In a throwaway clone `/tmp/jarvis-reviewer5-matrix-7e2d42b10a5c4f0e920f60cd878fb301` with **exact unchanged production** `48f1a8c6...a132`, I moved ONLY the mode-15 inbox/version snapshot **before** the corruption injection. That preserves the original operation, corrupted index, exact valid expected versions and both real bare-Git clients; no author's worktree, review ref, frozen test SHA, or production file was edited. Executed .NET SDK 11.0.100 Release matrix `FullyQualifiedName~ChallengeReverseIndexFaultMatrixTests`, Jarvis job `971a28fd7a8c49a9a2d7d3986341ca56`: **exit 1, 14 passed / 3 failed / 17 total**. Specifically mode 15:

1. Legitimately create assignment N=1, manually `blockAttempt(... challengeIds=[])`, capture the Blocked owner/attempt versions from a clean verified inbox.
2. Inject `challenge-waits/by-owner/{owner}/{task}/{attempt}/1.json` with a seemingly well-shaped locator, **never issued by a WaitOnChallenges with dependencies** and with no corresponding by-Challenge edge.
3. Call `ChallengeWorkflow.requeueAttempt` **directly**, using exact captured task/attempt/agent versions and generation1, without a preflight inbox read of the corrupted state.
4. **Actual: `Ok`, and genuine canonical bare-Git remote HEAD moves from `16f5bbbf98e15a459e7d1450b765e59565fa3eb1` to `dba78693b3ff716871d6ea074eaeaeffb66f7968`.** Expected: `LedgerIntegrityError` and HEAD unchanged. The test's mode15 counterexample explicitly reports `expected=Integrity actual=Ok`. This is a state-changing fail-open, unlike read-only mode16.

The source shows why: `requeueAttempt` calls `verifiedWaitIndexAt` **only if** the canonical wait dependency list is nonempty. Zero-dependency manual wait has no legitimate reverse edge; the forged owner edge is never inspected. Normal inbox verification detects it, but the public requeue operation does **not require** a preceding inbox read. Its CAS still commits, violating the task contract that corrupted index projections cannot influence a requeue/leave an index integrity diagnostic hidden.

## Required correction

Before **any** manual requeue, perform bounded, pinned-snapshot owner/index completeness verification regardless of whether the current canonical wait contains Challenge IDs. For a zero-dependency wait, require **absence of a current-generation owner/by-Challenge reverse edge**, while permitting all genuinely issued historical indices from earlier nonempty dependency generations. Preserve exact task/owner/attempt/generation CAS fencing, immutable receipt/audit lineage, valid `block []` and N->N+1 manual transitions. Never repair or delete corrupt ledger entries. Add a direct-operation two-clone negative regression with versions captured before corruption, assert unchanged verified HEAD/audits/notifications and positive manual control. Keep Worker 7 test-only lease untouched; its original mode15 preflight ordering itself needs separate test-author repair.

**No main publication or Scheduler/Health activation is approved by this review.**