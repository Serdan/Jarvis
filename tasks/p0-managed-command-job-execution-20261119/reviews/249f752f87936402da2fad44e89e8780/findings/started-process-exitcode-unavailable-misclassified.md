# P1: unavailable exit code for a started process is reported as FailedToStart

**Frozen source:** `249f752f87936402da2fad44e89e878064140c9f` (original base `a83549a6897a4e26aa4834ff81dc38b6e16ee85e`).

In `Client/Modules/JobManager.fs:165-178`, the `proc.Exited` handler runs *after a process was started and has exited*. If reading `proc.ExitCode` throws, line 177 assigns `JobStatus.FailedToStart "Process exited before an exit code was available."`. The scheduled consumer `JobManager.fs:805-812,892-912` then maps `FailedToStart` to `CommandSpawnFailed` instead of an unknown execution outcome. This cannot establish that no external side effect took place; the OS process was already running. No test injects an unavailable exit code after confirmed process admission.

This branch originated before this correction (git blame: `92181ad2a`), but it is still present in the exact reviewed source and is relevant to the P0 invariant that after-start uncertainty must never masquerade as definite spawn failure. If an already-executed process lacks a retrievable exit code, the correct terminal disposition is uncertain, not definitely unstarted.

**Required:** introduce/propagate an explicitly uncertain terminal outcome when an admitted process exits without a verifiable exit code, map it to `OutcomeUnknown`, and add a deterministic test at the status/consumer boundary. Preserve Worker 18's schedule persistence lease.

**Evidence limit:** conditional source proof; this was not reproduced under a real OS exit-code retrieval fault.