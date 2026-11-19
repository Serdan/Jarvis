# P1 — Scheduled run_command marks a live process as failed and never follows its job handle

**Frozen source:** `47376b7e4603694e803e7a75b7bdbcd6243f8253`; target base `a83549a6897a4e26aa4834ff81dc38b6e16ee85e`.

**Affected source (source inspection):** `Client/Modules/JobManager.fs:243-292` and `Client/Program.fs:196-225`.

**Cause:** The new `runManagedCommandForContext` deliberately returns after 1.2 seconds with `ProcessStatus = "Running"`, `ExitCode = -1` and a valid `JobId` for commands still executing. But `Client/Program.fs`'s scheduled `RunCommand` path still deserializes `RunCommandResult` and checks only `result.ExitCode = 0 && not result.TimedOut`. It immediately returns `Error(summary, "CommandFailed")` for *every* still-running process. It neither reads `ProcessStatus` nor uses `JobId` to poll `get_job_result`.

**Minimal scenario:** Schedule an allowed command lasting >1.2 s, e.g. `sleep 3` with a configured deadline above 3 s. The OS job is intentionally still running when the managed-command response arrives; the scheduled action records `CommandFailed` even if the process eventually exits 0. Any downstream scheduler retry, failure accounting or dependent action is therefore based on an incorrect terminal outcome; a retry may duplicate a still-running external effect. This is a causal source trace, **not** a live scheduler repro.

**Required correction:** Update the scheduled-command consumer in a separately coordinated change (Worker 18 owns scheduling) to recognize `Running` and poll the exact `JobId` until a verified terminal `JobStatus`, with explicit timeout/unknown/retention semantics. Only treat `Completed 0` as success; distinguish `Completed n`, `TimedOut`, `Canceled`, spawn failure and unknown/lost results. Add a regression that runs a >response-window scheduled command and verifies terminal success/failure without replay.

**Review disposition:** Integration-blocking until the managed command contract and this existing consumer agree. This review inspects the exact frozen commit; it does not change the author's source or merge anything.
