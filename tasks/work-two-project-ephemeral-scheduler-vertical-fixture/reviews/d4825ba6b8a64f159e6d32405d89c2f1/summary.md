# Reviewer 1 — independent frozen review: Accepted

**Task:** `work-two-project-ephemeral-scheduler-vertical-fixture`  
**Review:** `d4825ba6b8a64f159e6d32405d89c2f1`, claimed by Reviewer 1 at ledger sequence 5601  
**Exact frozen source:** `ee23942c8ef1322dc4e84a48fb7872e92a90b5f9`  
**Submitted base:** `bed92f3d2b39ef7c256a8d1955c48883af99cb60`  
**Prior rejected source/review:** `be31eb9e749948a2dea2e04b80f1c03b6828421a` / `a39e47ee22fc44a083a11a1c3c9ed32f`, ChangesRequested seq5560.

## Independent findings against all three prior blockers

1. **Original competing WQ07 CAS race now demonstrated.** `ConcurrentOriginalWq07CapOneFromTwoClonesHasSingleCasWinner` creates original disposable Git project/account remotes with independent clones. Each contender enters `AccountAdmissionAdapter.admitInertFixture` from a different account clone and project. A two-party barrier at `AccountCapacityLedger.withTestInterposition.BeforeLeasePush` ensures both original writers propose from the **same recorded account head** before either `--force-with-lease` push. Source inspection traced the interposition to the original `AccountCapacityLedger.transact` Git lease and retry. Assertions require exactly one `ProjectAdmitted`, one `GlobalReservationRejected(AccountCapacityFull(1, 1))`, one account state/sequence advance, a single held token, winning receipt, **no loser receipt**, no new WQ02 four-path credential from either direct inert-admission contender, and convergence of both original account/project clone heads. No fake provider is invoked in this direct admission race; the separate one-shot/unknown-send fixture checks dispatch behavior.

2. **Historical Agent/review OID preservation is no longer vacuous.** `withAuthorities` seeds an `agents/historical-agent.json` record and `tasks/historical-work/reviews/<id>/review.json` accepted review in **each actual original project Git ledger** via `LedgerStore.transact`, capturing both Git blob OIDs. `assertHistoricalUnchanged` re-fetches both clones, requires equal heads, exact original OIDs and original paths (no addition/removal) after the competing admission, admitted claim, checkpoint, terminal release, STOP and unknown-send, as well as at fixture teardown. Source worktrees are separately asserted unchanged.

3. **Stale WQ02 rejection now classified and mutation-fenced.** The stale second-clone `NativeWorkAction.Claim` must match exactly `Error(StaleEntityVersion "Stale WQ-02 project version.")`, traced to `WorkQueueGitLedger.change`'s original version guard. The test captures and compares pre/post original project commit, account head, canonical project version, receipt/launch counts, four-path credential verifier blob OID, and absence of the losing operation receipt. A generic preflight error cannot satisfy it.

## Independently executed local checks

Used fresh, clean **Reviewer 1 registered review worktree** `reviewer1-two-project-vertical-ee239-20261117`, SDK **.NET 11.0.100**, compiled `Client.Tests/Client.Tests.fsproj` in **Release** and executed actual NUnit cases (not just a successful build). Three previously existing F# warnings; no failures.

- Two-project vertical: **4/4**.
- Original WQ07 account capacity/admission: **27/27**, plus accepted-adapters capacity-one smoke **1/1** = **28/28**.
- WQ08 cross-project planner, scheduler host/head, preview and reactive wake: **81/81**.
- WQ11 native credential dispatch, durable launch intent, lifecycle and private host bridge fixture: **16/16**.
- WQ04 role authority and Integration source authority: **13/13**.
- **Total: 142 passed, 0 failed, 0 skipped.**

`git rev-parse HEAD` equals the exact frozen SHA; `git status --porcelain=v1` is empty; `git diff --check bed92f3d..ee23942c` passes. The difference from the previously rejected frozen SHA is **only** the vertical NUnit fixture and its documentation, not production source.

## Boundary and decision

All three seq5560 findings are materially resolved. The account remains `LegacyActive`; `Activated` is test-only in-memory projection; production `prepareProduction` remains `HostBridgeUnavailable`; review/Integration causal acceptance is inert. Neither a real provider, live Scheduler, production cutover nor hosted Actions/Codex was invoked. This review **does not** grant production Scheduler or migration GO and does not claim a full Server.Release integration run.

**Verdict: Accepted** for exactly frozen `ee23942c8ef1322dc4e84a48fb7872e92a90b5f9`, subject to the normal Lead-owned integration and remaining production gates.