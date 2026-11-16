# WQ06 frozen review — Changes requested

**Review ID:** `3b992a2446f14da8b7c02254cb62b849`  
**Source commit:** `e22675efb846e6bb86106bf6dc05a6bdfe2e0d5a`  
**Scope:** Manual Task/Role operator conversation CLI and Git-backed per-thread CAS; existing guarded WQ06 transport; project/test wiring.

## Evidence
- Read the frozen source, CLI, tests and documentation, and checked the ledger's replay-before-planning CAS path.
- Independently ran .NET 11 Release `WorkQueueOperatorConversationsTests`: **5 passed, 0 failed** (two-clone claim contention, generation fencing, durable replies, exact UUID replay, and raw repair audit).
- Independently ran Release `WorkQueueConversationTransportTests`: **0 passed, 4 failed**, exit code 1. These tests were newly included in the default `Client.Tests` project but assume producer/role authority not yet integrated; the existing fail-closed guard rejects the fixtures.
- No other confirmed blocking defect observed in the manual operator path.

## Decision
**Changes requested — one P1 test-suite regression.** See `findings/new-transport-tests-fail.md`. Keep protective WQ04/WQ03 checks intact; make fixtures valid or exclude/defer known-red dependency-gated transport tests from the default suite. Re-run Release tests on a corrected frozen commit.

The change remains manual and default-off; no Scheduler or production activation authorized.
