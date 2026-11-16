# Independent review — WQ08 practical dispatch prototype

**Verdict: Accepted** for exact frozen commit `7dcda43c9759559fbf35b5ce6f9022f8550d5a64` (base `9b918692af0330d6b7f2ce781cf81df37c7cec90`; review `bfa0a9910f4a4f68be0e41148d168901`).

## Assessment
No blocking defects found in the **isolated, default-off prototype**. Inspected the five-file commit, especially `Server/WorkQueueSchedulerPrototype.fs`, its NUnit tests and the recovery documentation, against existing `InertScheduler` and `Contracts` semantics.

- The direct one-tick path uses account reserve before project admission. A failed/unknown C01 does not release the reserved slot without the project abort-only terminal receipt. Lost project-admission replies with a committed launch stay explicit reconciliation cases.
- The injected original browser journal must durably CAS the one-shot marker before any submission. Existing, missing, or uncertain markers and unknown browser returns never produce a blind second send; recovered accepted receipts can replay the project-dispatch CAS. Browser and project transport exceptions are reported rather than silently interpreted as no effect.
- The same-execution first-connect retry and different-session rejection use the existing pinned WQ08 registration behavior. Recovery output surfaces launch/role/work, original dispatch ID, Git heads, source ref/commit, observed result, and manual Git backup/diff/repair steps.
- Source inspection (`git grep`) found no production call site for `SchedulerDispatchPrototype`. The WQ04 `canDispatch` guard remains default-off; there is no live Scheduler/browser registration, migration, deployment, or restart in this diff.

## Independent validation
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueSchedulerPrototypeTests`: **12/12 passed**.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore`: **275/275 passed**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueue`: **67/67 passed**, including client work-queue Git/CAS regressions.
- Reviewed the original browser exception/unknown/no-effect cases, C01-before-commit and lost-after-commit tests, and evidence retention on project/journal CAS failures.

## Boundary for subsequent integration (not blocking this review)
The ports are deliberately abstract; the real Git-backed source/role authority, global/project CAS binding, durable browser journal, WQ06 conversation registration, and production wake/activation still require separate work and operator authorization. The eventual adapter should distinguish definitive `NoCapacity`/CAS losing outcomes from transport-unknown reserves so ordinary contention is not unnecessarily presented as an unresolved effect. This is an integration consideration, not a defect in the disabled prototype.

**Decision applies only to the exact frozen SHA, not to production Scheduler activation.**