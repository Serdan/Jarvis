# P1 — Historical Challenge requeue lineage is accepted out of chronological generation order

**Task:** `scheduler-phase2-native-dispatch-20261121`  
**Independent re-review:** `435bd6824cd74c23b989709d667a656c` (Reviewer 4)  
**Frozen source:** `ef854433852b95a76b31d7af1465c5e08262a3a7`  
**Code:** `scripts/jarvis-phase2-snapshot-adapter.mjs` lines 266–294, 339–445, especially 393–419.

## Real disposable bare-Git counterexample

The source's authored N4→N5 requeue fixture has an active attempt at generation 5, canonical `challenge-wait.json` with dependencies `cccc…` and `dddd…`, by-owner and by-challenge reverse indices at immutable generation 4, and an authentic `RequeueChallengeBlockedAttempt` event at **sequence 98** (previousGeneration=4, newGeneration=5). Its two canonical Challenges are present.

Using the **actual frozen source** in the registered clean isolated worktree, the reviewer ran an ephemeral in-memory Node test (no source edits) with three genuine disposable bare Git remotes: one account authority and two project authorities. The additional historical by-challenge index was `challenge-waits/by-challenge/cccccccccccccccccccccccccccccccc/alpha/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/3.json`, with matching schema v2, task, attempt, owner and generation. The canonical Challenge ID and its `Created` event at seq95 both exist. The gen-3 `WaitOnChallenges` event at seq96 is valid. However, the gen-3→4 `RequeueChallengeBlockedAttempt` event was placed at **sequence 99**, deliberately **after** the current gen-4→5 requeue event at sequence 98, which is impossible for a single attempt's generation history. The fixture's `ledger.json.nextSequence` was adjusted to **101** to avoid a trivial next-sequence/header inconsistency; all changes were committed/pushed only to its disposable local bare project Git remote.

**Observed actual frozen behavior:** `snapshot.projects[0].completeness.challenges === true`, Challenge diagnostics `[]`, projected `waitIndexGeneration === 4`, `phase2DefaultOffDiagnostic(view).sideEffects === 0`. The independent rejection assertion expecting incomplete failed (`true !== false`), **OS exit 1**, 0/1 passing in the tightened single-case reproduction. Diagnostic output begins `REVIEWER4_CHRONOLOGY_PROOF` with `validLedgerNextSequence:101`, `olderRequeueAtSequence:99`, `currentRequeueAtSequence:98`, `completeness:true`, and `diagnostics:[]`. An initial independent three-case test separately showed that authentic N3 history stays complete and a nonexistent canonical Challenge is rejected (2/2 positive control cases pass), while the chronology rejection fails as expected (2 pass, 1 fail).

## Cause and effect

The new historical index validation checks the historical N block audit's shape and checks `oldRequeue.sequence > oldBlock.sequence` (lines 395–419), but **does not connect the historical requeue to the next generation's block/requeue or compare its sequence to the already-proven current N4→N5 requeue** (seq98). In particular, it permits a claimed N3→N4 transition at seq99 after N4→N5 at seq98, then reports the global reverse-index evidence complete. A syntactically valid sequence of individual event pairs is not proof of a chronologically possible attempt-generation chain.

This is a distinct residual P1 false-completeness defect within the exact historical N-to-N+1 lineage scope of this revision. It does not imply live Scheduler execution; this adapter is still explicitly default-off, read-only and cannot dispatch.

## Required correction

Prove chronological generation lineage: for each historical reverse index, require its matching block and N→N+1 requeue events to precede the already established successor generation's requeue (and any successor block, where available), with valid increasing sequences and matching task/attempt/owner. Do not infer historical legitimacy from a local block/requeue pair that postdates a newer-generation transition. Add this exact real disposable bare-Git regression (seq96 block N3, seq99 requeue N3→N4, seq98 current requeue N4→N5, `nextSequence:101`) plus positive preservation of authentic N3→N4 at seq97 and current N4→N5 at seq98. Preserve the newly corrected missing-Challenge, wrong-ID, foreign-task, absent/late-created-Challenge and default-off cases.

**Disposition: P1 — ChangesRequested.** Authored 72/72 Node, 21/21 .NET11 Release Client and 55/55 .NET11 Release Server pass, but do not cover or override the independent red chronology proof. No source mutation, production launch, publication, merge or activation performed.
