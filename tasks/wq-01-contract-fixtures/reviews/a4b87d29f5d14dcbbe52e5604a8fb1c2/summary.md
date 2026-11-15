# WQ-01 Phase A independent frozen re-review

**Reviewer:** Reviewer 2  
**Review ID:** `a4b87d29f5d14dcbbe52e5604a8fb1c2`  
**Exact source:** `143a73a35336305afaab7d796f16f70782982709` (accepted base `90ed868d04524d0483ccd979cffb5029d8cf7027`)  
**Prior ChangesRequested source:** `1b886591ae7e87158fc0debe2bf5f01858d17b7d`, verdict sequence 2876  
**Verdict:** **ChangesRequested** — new P1 FIFO violation and P2 blocked-state wake ambiguity. Do not integrate this source as an approved WQ-01 contract.

## Scope and independent validation

I inspected the full 1,249-line frozen `Server/WorkQueueContracts.fs`, its change from the prior frozen SHA (357 changed source lines), the changed tests and Phase A contract document, the earlier four findings, and the accepted architecture's exact FIFO and dependency requirements. The clean registered `worker5-wq-01-contract-fixtures` checkout points to this frozen source commit.

- .NET 11 `dotnet build Jarvis.slnx -c Release --no-restore`: **PASS**, 1 pre-existing/unrelated client FS0760 warning, 0 errors.
- Targeted Release `Server.Tests` WorkQueueContractTests: **41 passed**, 0 failed.
- Full Release `Server.Tests`: **176 passed**, 0 failed.
- Submitter also reported `git diff --check` PASS; I did not independently rerun that command. No Node files changed in this frozen diff and no Node test result is claimed.
- No source modification, merge, deploy, scheduler activation, browser operations, migration or restart performed by reviewer. This is pure Phase A contract code, not production scheduling.

## Four original P1 corrections

1. **Blocked wake:** ordinary, message, ancillary, waiver, cancellation and gate-resolve transitions now run a common dependency projection with one-shot CAS replay; however the fresh availability time is fabricated from the FIFO sequence, violating ordering.
2. **Demand invalidation:** partial maximum injective matching rematches compatible unclaimed launches, and unmatched Reserved/Submitted/Registered credits are fenced as `DemandInvalidatedPendingTerminal`; phase-specific terminal evidence and token release are modeled and tested.
3. **Operation replay payload:** `PublishStage`, `PostMessage`, `AdmitProject` use complete immutable typed payload equality, including nested dependency/source, subject/role-work, account token and role version. Different semantic inputs with the same operation ID now reject.
4. **Source handoff:** repository/task/lineage/stage/claim generation, expected ref/SHA and verified continuation ref/SHA are checked; the successor stage pins the continuation lineage. Real remote/job evidence is deliberately a future canonical integration requirement.

These changes materially address the original findings, but introduce/leave the following defects.

## Findings requiring correction

**P1 — `findings/01-fifo-wake-time.md`:** `wakeBlockedStages` stores `AvailableAt = NextAvailability + 1`, conflating independent server timestamp and sequence. `orderAvailable` sorts first by timestamp, so newly woken A can precede an older waiting C. Reproduction and precise expected/actual ordering are in the finding. Supply a real/injected virtual time witness to all wake-triggering CAS paths; add tests using timestamps intentionally unequal to small sequence numbers, including replay/stale-client behavior.

**P2 — `findings/02-blocked-projection.md`:** Explicit `Blocked`, clear-gate, empty-dependency stages are allowed at publication, but any unrelated dependency mutation wakes them because the scan tests every Blocked stage and empty dependencies satisfy vacuously. The existing `case_62_63` constructs exactly that state without asserting its preservation. Define valid Blocked causes or reject/normalize invalid publication and only auto-wake a truly resolved dependency block. Test unrelated transitions as well as legitimate completion wake.

## Deferred boundaries, not conflated with test success

True account/project Git-ledger CAS, durable canonical receipt encoding for all mutations, original dispatch/session proof, authoritative versioned waiver authorization, verified Git remote reachability/job journals, cross-project F06 producer/index fences and complete architecture §16 acceptance cases 1–80 remain later phases. Passing pure unit tests does not establish those production properties. Correct on a new exact source SHA and submit a **new independent frozen review**; do not change this frozen review reference.