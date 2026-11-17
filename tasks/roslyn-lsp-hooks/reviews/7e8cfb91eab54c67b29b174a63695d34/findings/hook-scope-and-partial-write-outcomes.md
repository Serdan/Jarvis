# P2 — Hook applicability and after-write failure-state reporting

**Frozen source:** `de596e2d0f0f70fbe35a72a2c8b822d2708941db`. These are source-inspected cases; no live reproduction is claimed.

## A. Unrelated path-limited commits require approval even when no hook matches

At `Client/Modules/ClientShell.fs:265-273`, `preCommitEnabled` means merely “there is at least one enabled pre-commit hook in the configuration” whenever safe paths are nonempty. `requireApproval root` then executes *before* the actual changed-file set is resolved. With an enabled `.cs` formatter but a requested `README.md`-only commit, an unapproved checkout is refused even though `CodeQualityHooks.run` would find no matching hook.

This is inconsistent with scoped extension matching and unnecessary for preventing analyzer execution on unrelated files. Scope the approval requirement to the actual selected matching paths while retaining the fail-before-mutation property for selected hooked paths; add an unapproved nonmatching-file commit test.

## B. A blocking after-write failure is reported as a failed operation after the write succeeded

`Client/Modules/ProjectBrowser.fs:1206-1221` checks approval, executes the write/patch operation, then runs formatting. If the server is missing or formatting errors and the policy is blocking, `CodeQualityHooks.run` returns `Error` (see `Client/Modules/CodeQualityHooks.fs:299-300`), dropping the successful original write result. The user receives a failed write response although the original edit is already persisted; retrying with the original expected hash can now conflict.

No rollback requirement is assumed here. The minimum correction is to expose a structured **write persisted / hook failed** outcome (including hook name, path, actual after-hash when available) or explicitly guarantee rollback/atomicity. Add a deterministic missing-server case and protect against unexpected duplicate writes on retry.

**Severity:** P2; independent of the P1 prewarm gap and unverified acceptance cases.