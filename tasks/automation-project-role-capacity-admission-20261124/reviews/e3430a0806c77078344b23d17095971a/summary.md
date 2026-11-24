## Verdict: Accepted for the default-off disposable seam

Reviewed immutable head e3430a0806c77078344b23d17095971a5fe2edde against base cb3b2f54a9f586228d5b2b210f1be6600099f932 and the canonical task spec. No blocking defect found within the expressly limited seam. The production project does not include this module; initialization and send/exclusion transitions are compiled only with the disposable-test symbol. LedgerStore.transact checks the request hash before replay and re-plans against the fetched snapshot on contention; the planner counts Held and Dispatching entries, validates binding and original identity, and rejects unknown initial state. The integration contract explicitly disclaims full sender coverage, trusted identity and terminal evidence.

## Independent verification

In an isolated worktree at the exact head, `dotnet test tests/ProjectCapacityDisposable20261124/ProjectCapacityDisposable20261124.fsproj -c Release --nologo` passed 5/5 tests; `dotnet build tests/ProjectCapacityProductionCompile20261124/ProjectCapacityProductionCompile20261124.fsproj -c Release --nologo` succeeded. Existing Client Program.fs warning FS0760 and test project warning FS0988 were emitted. No live reservation, provider send, deployment, or scheduler activation was performed.

## Evidence limits and integration obligations

The tests use two clones of disposable Git authorities, not live clients or real provider effects. They do not prove restart recovery, authoritative bootstrap, trusted role/effect issuance, verified terminal release, or exhaustive bypass fencing; those remain explicit future integration obligations. The single state blob retains excluded entries and LedgerStore enforces a 64 KiB entity limit; a production design must address bounded growth while preserving original-operation and effect replay protection. This is a future integration constraint, not a claim that the isolated fixture enforces a live capacity domain.
