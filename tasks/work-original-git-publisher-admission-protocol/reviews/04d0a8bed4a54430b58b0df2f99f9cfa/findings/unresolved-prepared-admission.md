# Blocking P1b finding: a live publisher may admit another CAS after uncertain-outcome journal failure

**Frozen reviewed commit:** `effa96ff46d9e5da9c0a6155c7698dd973d84c75` (base `b80613fa7dd5650c99985582a10085ad8233471d`). **Scope:** default-off disposable fixture, not hosted C3/production authority.

## Failure mode

- `Server/OriginalGitPublisherExecutor.fs:121-125` catches **every** `store.MarkUnknown` exception and still returns `OutcomeUnknown`. The comment relies on converting a remaining `Prepared` row to `RecoveryHold` on the **next restart**, but there is no fail-closed transition for the current still-running store instance.
- `Server/OriginalGitPublisherStore.fs:206-219` attempts to atomically persist `unknown` + `phase='recovery'`; a transient SQLite open/transaction/commit failure can leave the committed original `Prepared` row and `publisher_state.phase='active'`. This is especially relevant after `ExactRemoteCas` may already have advanced the remote ref.
- `Server/OriginalGitPublisherStore.fs:171-187` permits a **different UUID** whenever `allowMutation` sees `Active`; it never checks for an outstanding `Prepared` or `Unknown` row. Consequently, after a failed `MarkUnknown` followed by recovered database availability, a new `PublishCas` can prepare and execute a further remote CAS *without* restarting or reconciling the unresolved first effect. The single-process lane serializes calls but does not poison admission between calls.

This is source-demonstrable: `Prepare(A, hashA)` returning `Fresh` does not prevent `Prepare(B, hashB)` also returning `Fresh` while A remains `Prepared`. That state becomes externally reachable through the swallowed `MarkUnknown` failure. `BeginFence` separately counts unresolved rows and therefore refuses a positive barrier; **that protection does not prevent new CAS publications**. The design requires unresolved external effects to hold all further original publishing, not merely a future barrier or restart.

## Narrow correction and regression

Fail closed on *fresh* publisher admission whenever the journal contains any unresolved `Prepared`/`Unknown` operation, regardless of the phase row; retain an in-process hold if a journal write fails and require explicit evidence-backed recovery before admitting new mutations. Keep exact same-UUID replay nonpublishing. Add deterministic tests for unresolved `Prepared` A versus new UUID B, and for an injected transient `MarkUnknown` persistence failure after a possible remote effect: B must not invoke `ExactRemoteCas` before or after connectivity recovers, while restart must continue to hold. Preserve the currently corrected typed-stale, operator-decision replay, source-effect and barrier-race tests.

**Boundary:** this requests no original Git client edits, hosted credentials, live hooks, P1c/C3 cutover or production GO. The disposable pre-receive denial remains *not* an all-client remote authority witness.
