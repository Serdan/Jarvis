# WQ04 combined corrected-source — Reviewer 2 independent verdict: **ChangesRequested**

- **Canonical review:** `fbf33be4bb0c4dff92a7415f4827db0a` (task-bound `wq-04-queue-producers-priority-roles`), claimed Reviewer 2 seq3647.
- **Frozen exact SHA:** `5e2c1b4164204145653d957bd4adfcf24f597c0a`, two parents exactly first integrated `e14173e116dc03b7dbdf261895fde3c893a643ee`, second Worker4 correction `29b5ae1ee3ea579824ea0f19cab9d902d7713802` (direct child of previously rejected `6f068672...`). Independent automatic merge-tree and HEAD tree both `96006be776ce72737ae8df32d1ae83a7c0f95b80`; `git diff --check` clean; worktree clean.
- **Independently executed .NET11 Release:** SDK 11.0.100; Server WorkQueue **98/98 pass**; Client WorkQueue + SourceEffect + AgentDashboardCache + AgentView **103/103 pass**; zero failed/skipped. Tests include existing disposable two-clone CAS negatives. Existing FS0760 warning Client/Program.fs:188 only.
- **Resolved *within their bounded inert routes*:** `ProduceEvent` unconditionally rejects caller-authored events/high-water (both mutation and source-proof guard); `selectRoleDemand` unconditionally fails closed when immutable source/index and CAS-pinned role authority are absent; pure policy successor ID is stable across caller SourceId/receipt and distinct operation IDs; hard prerequisite removal wakes with new FIFO witness. None of these establishes production source authority or Scheduler admission.

## Blocking finding — legacy semantic bypass of the new namespace-only fence

`Client/Modules/WorkQueueGitLedger.fs:925-932,940-949,920-922,950-962,979-982` treats a `wq04-` **ID prefix** as the signal of unverified WQ04 work. An otherwise identical `FrozenReview`, `Integration`, `Planning` or `RoleMessage` record with a different, nonblank ID passes the older `PublishStage` or `PostMessage` validation (`validInitialStage:742-758`; `Contracts.publishStage:569-595`; `Contracts.postMessage:610-629`) without canonical event/Accepted-review proof, and evades the prefix-only `AdmitProject`/`ClaimNext` fence. The old WQ02 legacy admission predicate is still unversioned. The new two-clone negative `WorkQueueProducerGitTests.fs:211-229` tests only the prefixed stage, so it does **not** detect this semantic bypass. This is a direct static-source counterexample, not a claimed new executable exploit probe; current runtime Scheduler remains off.

See **`findings/reviewer2-independent-corrected-source-audit.md`** for complete source references, test evidence, corrected F01–F04 assessment, and precise remediation.

## Canonical decision

**ChangesRequested.** Do not integrate this frozen WQ04 source as a completed canonical producer/admission safety boundary. Fence unverified work by durable canonical origin/receipt/capability across **all** ingress and admission/claim paths, rather than caller-controlled ID; preserve legacy WQ02 prototype behavior only if distinctly non-authoritative and non-activatable. Add two-clone renamed-work negative cases (FrozenReview, Integration, RoleMessage, admission/claim and CAS races), freeze a new exact SHA, obtain a new task-bound independent review.

WQ03 host/WIP terminal-source proof, WQ05 authoritative adapter and live Scheduler remain blocked. No GitHub Actions, main push, Scheduler activation, deployment, migration or Codex.