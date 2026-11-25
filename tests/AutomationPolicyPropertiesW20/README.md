Bounded policy history increment. Two deterministic seeds each exercise 18 alternating two-clone operations on a disposable real Git ledger. The independent model tracks only the accepted revision, total and role map; stale writes, request-bound retries, changed-request replay, unknown roles and wrong revisions must fail closed. Assertion messages record seed and step. Run: dotnet test tests/AutomationPolicyPropertiesW20/AutomationPolicyPropertiesW20.fsproj -c Release --filter FullyQualifiedName~AutomationPolicyPropertiesW20

This exercises the integrated default-off policy producer only, not real reservation occupancy, runtime admission, Health/Challenge/Scheduler composition, or release acceptance. Fixed seed histories are reproducible but are not shrinking or concurrency coverage. Existing W14/W16 tests cover their own separate cases; their fixture cannot establish actual occupancy.

ConcurrentPolicyWritersHaveOneSuccessor races distinct policy values from the same expected revision through two disposable Git clones. Either winner is legal; exactly one succeeds, both clones observe its complete policy, and exact retries do not create another revision. Release job 8dd814264d2647f6bf3fa87771ac99f0 passed all 10 selected cases, exit 0. This is a scheduling race, not proof of both CAS interleavings or composed runtime admission.

## Current optional-policy parity (2026-11-25)

The historical mandatory-policy producer described above is no longer the
ordinary Scheduler policy. On exact integrated production source
7a9ffa965b13f95a071cb125edbaa24cbe76f320, job
1a7abd7fcd724ce8b87bc29053910509 was RED (3 old property cases failed,
8 separately linked legacy capacity cases passed). The old fixture never
registered canonical roles, and the oracle required mandatory schema-1
dimensions. Do not weaken current role checks to make that harness green.

The rebind uses the actual compiled Client assembly and the existing hosted
disposable Git fixture, registering canonical roles via RoleInstructions.
The independent model starts with disabled optional dimensions and tracks
revision and total/role maps. A guided prefix exercises both off, total
only, role only, both on, and explicit removal; seeds 17/41 generate a
further mixed suffix (24 steps per seed). Both clones must agree after each
step; stale writes and altered original-UUID requests cannot advance the
head. A two-clone race permits either distinct optional policy successor,
but never two. The legacy schema-1 compatibility case verifies exact old
receipt replay through upgrade, disabled schema-2 state, and no downgrade.
The old disposable reservation suite remains separately available at
tests/ProjectCapacityDisposable20261124; it is not a model of the new
Scheduler-only Working-count policy and is no longer linked into this
property runner.

Release job 70f75a22440e4276b9274d15bb1cb66c passed 4/4 (22 s test duration,
exit 0), actual Client built on the exact production base above.
Command: dotnet test tests/AutomationPolicyPropertiesW20/AutomationPolicyPropertiesW20.fsproj -c Release -p:BuildProjectReferences=false
(using the already rebuilt exact Client reference). TRX:
final-optional-policy-delta.trx. This is source-level isolated evidence;
no live configuration or deployment, and no assertion about browser send,
reservation occupancy, or production automation activation.
