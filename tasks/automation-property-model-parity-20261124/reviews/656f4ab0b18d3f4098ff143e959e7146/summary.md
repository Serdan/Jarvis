## Author summary

Two deterministic 18-step histories compare accepted policy revision, values, stale writes and request-bound replay across disposable clones against an independent small model. Release filtered run passed 2/2, 6s test duration. Based on integrated 3f84149a; does not prove occupancy, runtime admission or milestone composition.

## Independent review

Reviewed immutable commit 656f4ab0b18d3f4098ff143e959e7146482e5544 against base 3f84149a29950830d5bb217fa341611d95bfe958. Accept the stated incremental real-ledger policy-history test, not the holistic milestone. The property independently tracks accepted revision, total, and roles across two disposable Git clones; it checks stale writes, idempotent request-bound retry, changed request reuse, wrong revisions and absent roles, with state observed after each step. No blocking defect found in this bounded increment.

Independently ran `dotnet test tests/AutomationPolicyPropertiesW20/AutomationPolicyPropertiesW20.fsproj -c Release --filter FullyQualifiedName~AutomationPolicyPropertiesW20` in a detached checkout of the exact SHA: exit 0, 9 passed, 0 failed, 0 skipped (10 seconds reported test duration). The linked fixture contributes additional tests to that count; it does not mean nine generated histories. Build emitted FS0760 in Client/Program.fs and FS0988 for the test project.

Evidence limits: two fixed seeds, 18 sequential steps apiece; no shrinking, negative controls, generated concurrent schedules, reservation/effect lifecycle, runtime admission, or cross-feature Health/Challenge/Scheduler composition. The policy source is linked into the test assembly and is default-off, so this does not establish deployed runtime behavior. README correctly records these as outstanding; exact composed-source execution and the task's broader milestone requirements remain open. Generated GUIDs are nondeterministic, though seed and step reproduce the policy value/control sequence.