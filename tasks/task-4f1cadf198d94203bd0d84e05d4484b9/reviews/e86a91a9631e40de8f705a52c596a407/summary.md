# Independent review — server updater production parity for protocol 3.63

**Verdict: RequestChanges (one P2 runbook correctness gap).** Updater implementation and verified black-box behavior appear correct.

Frozen source `aef4da9e97bd27682bd85e42f18d635c25cf4ebe`, branch `refs/heads/fix/server-updater-prod-parity`, base `28fd0c3d2fa887a8cd7289e07cb65b476de351f9`, target `main`.

## Source behavior independently inspected

- Corrected direct unproxied Kestrel readiness to **127.0.0.1:5095**, consistent with verified production listener; no proxy, redirect or bearer-token fallback. The same service MainPID must be active, have the exact expected executable SHA-256 via /proc/PID/exe, and own the loopback TCP listener in bounded `ss` output. HTTP endpoints must prove OAuth protected resource metadata, JSON-content-type POST /mcp 401 Bearer, and SignalR negotiate 401 with client:connect. PID is rechecked after probes.
- Graceful stop uses **STOP_TIMEOUT_SECONDS=100** in both forward update and rollback, above the known production systemd 90-second stop budget. Distinct restart timeout remains 30 seconds after explicit stop. The 35-second bounded readiness probe applies separately to candidate and recovered prior binary.
- Preflight failure aborts before service mutation, recording not-needed rollback. After a verified backup and private journal, failures restore old hashed binary, restart, independently verify prior PID/port/routes, and retain journal on incomplete recovery. SIGKILL/power loss journal is recovered on next invocation; success clears it only after positive readiness.
- Installer comment correctly reminds operators to install the root-owned wrapper that provisions sudoers and the private pending directory, not only the helper.

## P2 issue — operator runbook contradicts production constants

`findings/p2-prod-runbook-drift.md`: `docs/ServerUpdates.md:10,22` continues to tell administrators that the helper listens/probes on port **5000** and to verify that constant before installation or emergency recovery; the reviewed correction and verified production target **5095**. Line 54 describes protocol **3.62** as a future separate rollout though production is already at **3.63**. The runbook also omits the significant JSON media-type and 100-second graceful stop details. Update this operationally important documentation to match the actual verified source; do not change the correct 5095 code to follow stale docs.

## Independent verification

- Full black-box updater fixture **21 passed / 0 failed**, job `623233603258430bacf2766d4035572d` (60.8 s). It uses temp paths, synthetic /proc, fake service PID/listener ownership and HTTP; no production service or systemctl touched.
- Negative tests verify incorrect 5000 probe or omitted JSON Content-Type rejects preflight with zero stops, unchanged live binary and no journal, plus graceful-stop budget and rollback path. Existing negative tests cover wrong PID/port ownership, checksum/manifest and delayed readiness.
- Frozen source HEAD and worktree clean; `git diff --check` passes; `git merge-tree --write-tree main aef4da9e` exits 0 with no conflicts.
- No production execution, integration or deployment was performed by reviewer.

**Request changes only for runbook parity**, then resubmit source. No claim of a newly discovered live binary or rollback defect.
