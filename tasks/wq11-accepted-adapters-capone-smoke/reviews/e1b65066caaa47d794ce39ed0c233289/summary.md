# Reviewer 2 — independent review of WQ11 accepted-adapter cap-one fake-only smoke

**Verdict: Accept — no blocking findings.** Reviewed frozen commit `944bca7fa38cb5e973b04ce7829966cca351202b` against `b277cd0d1b850b97ad81d889e2278aaef74e74ae`, with only `Client.Tests/Client.Tests.fsproj`, `Client.Tests/WorkQueueAcceptedAdaptersCapOneSmokeTests.fs`, and `docs/WorkQueueWQ11AcceptedAdaptersCapOne.md` changed.

## Independent verification
- .NET 11 Release focused test `TwoRealProjectsOneGlobalCapPinnedAdmissionAndOneShotOriginal`: **1/1 passed** (12 seconds).
- Adjacent Release regressions selected across WQ11 prior fake acceptance, WQ03 original journal, WQ04 practical admission, WQ08 cross-project planner, WQ07 account adapter, canonical role/source/binder tests: **82/82 passed**, no failures.
- `git show --format= --check 944bca7f...`: clean, exit code 0.
- Frozen review worktree tracked status: clean.

## Examined behavior
Two real disposable WQ02 project remote/clone pairs and an independent cap-one account authority; original CreateTask receipt and pinned role/source witness; blocking-first WQ08 fake-only two-project planner selection; explicit WQ04 admit/replay/cap-one rejection; WQ03 provisioned host continuity pre-send marker, reopen, single fake callback, replay/foreign UUID conflict; WQ02 first-connect/claim/terminal proof; rejected forged terminal and exact idempotent account release; second project's unknown callback holds token and forbids redispatch. Negative planner evidence for stale role, duplicate browser UUID and unrecorded original is non-dispatchable. Temporary git/host resources cleaned up.

## Explicit evidence boundary
The WQ07 fixture's real account is `LegacyActive` and is correctly denied by the unmodified WQ08 read-only planner. For positive **ranking only**, the test injects an in-memory hypothetical `Activated` marker; real WQ04 admission/release uses the unmodified Git account. This validates the fake-only accepted-adapter seams, **not** migrated production activation, live browser/host dispatch, or a scheduler. Docs and test explicitly disclose this boundary. Missing original journal evidence is `Unknown` rather than `NeedsAttention`, consistent with accepted WQ08 planner semantics; both fail closed.

No production adapter, Scheduler, WQ03/WQ04/WQ08 implementation, or live transport changes were in this review.
