# P1 — Prepared external effect prevents durable Recovery seeding

**Location:** `Server/WorkRecoverySqlite.fs`, `validate`, `state.Works |> List.exists` branch testing `row.Value.ExternalEffect = WorkSqliteEffect.Prepared` (frozen `4d6d6f07b43a3641b0b6162ecf91411868ef9045`).

**Problem.** A preserved stage with a `Prepared` external effect causes `seed` to return `Incomplete` *before* the SQLite fencing transaction. The project remains `coverage=Verified`; no Recovery marker is written. E0's `verifyProject` verifies stage metadata but does not prohibit a `Prepared` effect on a different stage (`Server/WorkSchedulerSqlite.fs` lines 163–185, 228–261). `reserveFresh` filters only the selected candidate's unsettled effect (lines 423–428), so with stages A=Prepared and B=Available/settled, B remains eligible for new launch. Thus an uncertain original external effect prevents, rather than triggers, the required *project-wide* durable Recovery fence.

**Required correction.** Treat `Prepared` as uncertainty to preserve and fence, not as a reason to leave an otherwise verified source runnable. Seed a single guarded Recovery transition for every affected project/stage while retaining original effect evidence, or establish an equivalent durable fail-closed admission barrier before returning the error. Do not change the original effect to a terminal or negative outcome.

**Negative regression:** one project with A=Prepared, B=Available/NoneRecorded and valid role/capacity. After calling `seed`, assert all sibling Scheduler reserve paths fail, the project has durable Recovery coverage, the original A effect remains Prepared, and crash/replay cannot reopen B. Distinguish genuinely invalid/corrupt source rows (where caller must not dispatch) from normal in-flight Prepared evidence.
