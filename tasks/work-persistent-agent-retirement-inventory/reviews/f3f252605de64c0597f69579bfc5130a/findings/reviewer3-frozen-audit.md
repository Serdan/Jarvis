# Reviewer 3 — independent frozen audit

**Review:** f3f252605de64c0597f69579bfc5130a  
**Frozen source:** `8ee63471e7fa153fd42f489a9be227d5701b3fe1`  
**Base/parent:** `3458224ce3bff9141b05a7891148098db4900053`  
**Verdict:** **Accepted — diagnostic documentation only; production Agent-to-Work cutover is NO-GO.**

## Provenance and scope checked independently

- `git rev-list --parents -n 1 8ee63471` returned exactly the stated parent `3458224...`.
- `git diff --name-status 3458224... 8ee63471...` returned only `A docs/work-persistent-agent-retirement-inventory.md`. `git show --stat` confirmed 68 additions in that one Markdown file; no production, test, scheduler, DB or service code changed.
- `git diff --check 3458224... 8ee63471...` exited 0 with no diagnostics. The frozen document was read directly via `git show 8ee63471:docs/work-persistent-agent-retirement-inventory.md`. `dotnet --version` on the local Jarvis client returned `11.0.100`.
- No .NET Release tests were rerun in this independent documentation review because the frozen diff contains no executable/test source. The submitter's summary claims focused WorkQueueToolFacade and WorkQueueCanonicalMigrationPrepare Release tests 18/18; that claim is not represented as my own verification.

## Source-grounded spot checks at the exact frozen SHA

- **Persistent Agent writers/readers:** `Client/Modules/ReviewWorkflow.fs` defines `registerAgentAs` (1654), `startTasklessWork` (1679), `yieldTasklessWork` (1700), `assignTaskAs` (1744), `inspectAutomaticActivation` (3081), `findAgentBySessionWhere` (3111) and `getAgentInboxAt` (3135). Related `notifyAgent` (1460) and `queueResponseAlert` (1554) remain Agent-addressed.
- **Roles/challenges:** `RoleInstructions.fs` defines `requireRegisteredAgent` (188), called from `attachRole` (253–262), `setAgentInstructions` (296–306), and `resolveAtKnownAgent` (351); `ChallengeWorkflow.fs` includes `subscribedAt` (655) and `setSubscription` (671). This supports the inventory's legacy identity-dependency map.
- **External side effects:** `Server/McpTools.fs` still exports AgentInbox, ObserveAgent and RegisterAgent (1695–1718). `Client/SignalR/Client.fs` invokes `activateReadyOnActivity` (1123). `BrowserBridge.fs` has `requireAgentCreated` checking and enforcement (431–457,829).
- **Work-side NO-GO:** `WorkQueueToolFacade.fs` exposes `MissingClaimBinding` (69), `MissingTerminalBinding` (73) and `transitionWithBinding` (250) returning `RejectedMissingBinding`, `Applied=false` (272). `WorkQueueCanonicalMigrationPrepare.fs` declares `AuthenticatedOldWriterFenceMissing` (56) and `AccountPreparingCasUnavailable` (59); `inspect` (290) returns `CanonicalPrepared=false` (372). `WorkQueueCanonicalCasBinder.fs` defines `publishFreshPractical` (214) and fills `RoleId = ""`, `RoleVersion = 0` (296–297); `WorkQueueGitLedger.fs` checks those empty role fields (986). The documented negative-only diagnostic in `docs/WorkCompositionQueuePositiveCasNoGo.md` expressly reports **zero positive** same-transaction binder acceptance.
- **Scheduler and projection:** `WorkQueueSchedulerHostLoop.fs` has `enabled = defaultArg enabled false` (48); `ConsoleTui.fs` renders "Work dashboard - READ ONLY; claim and Scheduler dispatch are disabled" (1496). Legacy `BrowserSchedules.fs` retains `AfterAgentIdle` and `IdleAgentId`; `Client/Program.fs` returns `ActivityProofUnavailable` (355–359). `OriginalBrowserJournal.fs` contains the claimed original-browser sqlite and continuity anchor names (48–49), while `WorkQueueOperatorConversations.fs` defines durable thread path (77–78).

## Decision boundary

The inventory distinguishes existing code from the desired Work contract, requires preservation of historical Agent/review/assignment data, and orders old-writer fencing, reversible account/project preparation, original same-CAS provenance/graph/queue publication, launch/credential/STOP recovery, Work API/TUI binding, empty-server restart evidence, and explicit operator GO/NO-GO. No step is falsely marked complete. **Accept the audit as a source-grounded diagnostic map only.** It establishes neither a working Work writer nor live Scheduler/readiness/cutover authorization. Source anchors are pinned to the stated older baseline, not asserted as a guarantee about later main revisions.

**Findings requiring change in this documentation-only review:** None.
