# Feature: Verify and integrate outcomes

- **Identity:** `telos.feature.verify-outcomes`
- **Systems:** [Work](../systems/work.md), [Execution](../systems/execution.md), [Evidence](../systems/evidence.md)
- **Status:** Proposed enduring Feature contract

## Desired behavior

For proposed source changes, Telos should record exactly what was built and tested, request an **independent review of an immutable source candidate**, and control integration as a distinct decision. It should explain what the resulting evidence does and does not establish about the **current** Project/System/Feature contracts.

A review that requests changes is a completed, legitimate review Activity, not an accepted implementation. The next revision has a distinct source identity, may require more testing and must receive its own appropriate independent verdict. No implementation author should be able to accept their own change by changing role labels.

A candidate that passed tests and review does not become mainline source merely because the tests were green. Integrating it checks the actual target branch, version and applicable acceptance evidence, avoids force-overwriting concurrent changes, and reconciles uncertain Git effects rather than blindly retrying. A successful merge does not automatically prove the whole product promise.

Telos should make the evidence chain legible: contract criterion → relevant test/profile and actual execution → source/SDK/environment receipt → independent decision → integration result → current conformance. Missing, stale, partially verified, expected-failing and contradictory evidence must remain distinguishable.

## Observable satisfaction

A reviewer requests a correction on source A. Source A remains unaccepted even if its author reruns an unrelated test successfully. Source B receives a separate exact-source review, and only its independently accepted, eligible version can enter the integration process. If the target branch changes or the push acknowledgement is uncertain, Telos blocks a blind repeat and reports the exact unresolved state. The Operator can distinguish review acceptance, integrated source and current Feature satisfaction.

**Not implied:** mandatory complete release verification for every disposable prototype, a second review verdict fabricated by the Integrator, or automatic deployment after integration.
