# Scheduler Working-count check

The Operator's 2026-11-25 clarification scopes optional project/role limits exclusively to Scheduler launches. Manual launches never call this module, even when policy or ledger reads fail. Once they are canonically Working, they contribute to Scheduler counts exactly like all other agents.

## Caller contract

Compile `Client/Modules/ProjectWorkingCount.fs` after LedgerStore, ReviewWorkflow and RoleInstructions. W19 owns runtime registration and invocation; W23 owns the manual caller and must not invoke this module.

`ProjectWorkingCount.isHeldAt binding snapshot targetRole total roles : Result<bool, LedgerFailure>`

Use the existing verified canonical project snapshot and successfully decoded optional policy (W13 `OptionalPolicy.Total` and `Roles`). Do not convert a failed configured-policy read into empty limits. The target role comes from the existing scheduled candidate's canonical role selection. Empty limits (or absent applicable role cap and no total cap) do not enumerate agents. `Ok true` means capacity hold; `Ok false` means no capacity hold, not dispatch permission. `Error` means a failed enabled check; defer the scheduled action rather than bypass it. Existing authorization, STOP, challenge eligibility, effect integrity and duplicate-send gates remain independent.

Total counts each registered agent whose derived canonical state is Working, including taskless work, once. Role counts each such agent attached to the target role, including agents with additional roles. Ready, Parked and NeedsWork are not Working. Neither active session IDs nor provider/terminal inventory participate. The same pinned snapshot supplies state and role membership. Limits are positive; equality or excess holds a matching new scheduled launch. There is no reservation, CAS, generation-bound occupancy witness or strict no-overshoot promise; in-flight or manual launches and cap changes can temporarily exceed limits, and later checks drain naturally as Working turns end.

## Focused verification

`dotnet test tests/ProjectWorkingCount20261125/ProjectWorkingCount20261125.fsproj -c Release --filter FullyQualifiedName~ProjectWorkingCountTests`

Tests use two independent clones of a disposable bare Git remote, real canonical registration, role attachment, task assignment and taskless start/yield transitions. No live dispatch or deployment.
