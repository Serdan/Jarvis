# Independent review — ChangesRequested

**Review:** `f25b69d48f814391bb1da115d89fca11` · **Worker:** Worker 11 · **Reviewer:** Reviewer 3  
**Pinned source:** `6d1f003aec6f097dae2d35b347f51263a46dd9db`, branch `feat/concurrent-client-instance-identity-worker11`  
**Task:** `task-cff673cd81474ccf9b3932bf0ea33109`, support concurrent Jarvis client instances  
**Decision scope:** exact frozen source, not current main, not approval to deploy/merge.

## Findings requiring correction

1. **P1 — New process route strands default after restart.** `findings/instance-default-lifecycle.md`. New clients receive a new route per process, while old disconnected default and conversation bindings remain selected for the retention window. Normal exit and restart on the same machine can therefore leave implicit commands failing despite an active replacement. Clarify and test safe unambiguous replacement handling, without retargeting explicit instance requests.
2. **P1 — Legacy-to-v2 migration silently chooses one of several live instances.** `findings/ambiguous-legacy-migration.md`. `UserService.migrateDisconnectedLegacySelections` picks the oldest matching active route, and overwrites conversation/default selection without proof that it is the intended process. A concurrent same-machine cutover can route stateful commands to the wrong instance. Reject ambiguous migration and test it.
3. **P2 — Shared JSONL activity writer/rotation is not inter-process coordinated.** `findings/shared-activity-log.md`. The added `agent-seen.json` merge lock does not protect the machine-shared activity log segment selection, append and rotation. Add a proven concurrent-safe design and multiprocess stress regression.

## Correctly scoped elements

Versioned machine/installation/instance routing, atomic GUID creation, malformed identity rejection, clone collision refusal when different installation IDs are simultaneously known, cross-process OAuth/config/browser-key locks, process-local pending-job handle binding, and existing pending-operation journal/Git CAS invariants are directionally sound. The documentation correctly admits that **fully cloned machine + installation files**, and registration when the ephemeral server registry has forgotten a peer, cannot be distinguished; this remains an important *acceptance boundary* requiring explicit operator disposition rather than a claim of globally guaranteed machine uniqueness. No server persistent DB cutover or deployment observed.

## Actual verification

- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~ServerDeviceRouteIdentityTests`: **5 passed**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~ClientDeviceIdentityTests|FullyQualifiedName~concurrently_started_clients_merge_agent_seen_snapshots_without_losing_evidence'`: **6 passed**.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --no-restore`: **493 passed**, none skipped/failed.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore`: **timed out at 180 seconds** (`ExitCode=-1, TimedOut=true`); no final pass/fail counts. Activity persistence diagnostics appeared, but no causal attribution is possible from that console output alone.
- Source frozen worktree remained clean after checks. **Not exercised:** two actual concurrent JarvisClient processes against one server, real server restart, clone-machine integration, browser-bridge port routing or activity JSONL stress. No author source edits, merges or deployments by reviewer.

The frozen feature commit is the single child of main parent `ffd2bb3a153cf14e15ed2105e12e8179a540f94a`. The unrelated ChatGPT browser launcher visible when diffing the older review BaseCommit is from the parent main commit, not this feature's authored changes. Verdict addresses only `6d1f003a`.

**Conclusion:** ChangesRequested for exact frozen review. Regression tests should exercise both ambiguous and unambiguous lifecycle/selection transitions and simultaneous shared-file writers. Re-review a new frozen corrected commit; passing current unit tests does not establish these missing safety cases.
