# WQ10 default-off old-writer protocol fence (NO-GO)

Task: work-wq10-legacy-writer-protocol-fence. This change is a verified Git-head-scoped **negative guard**, not a production migration or an account/project conversion authorization.

## What is fenced

The opt-in host boundary in Client/Modules/LegacyWriterProtocolFence.fs validates one schema-v1/protocol-v1 host binding; compares its approved project identity, ledger/epoch and fetch/push remotes against the existing configured project trust binding; and freshly fetches the original WQ07 account and WQ02 project Git authorities, including their state identity/integrity and exact expected heads. The host opt-in is JARVIS_WQ10_LEGACY_WRITER_FENCE_BINDING, an absolute regular, non-symlinked, non-group/other-writable JSON file with project/account identity, remote endpoints and pinned original Git heads. An unset variable is DEFAULT OFF. A host file is not, by itself, a cryptographic operator authorization or a fleet-wide deployment proof.

LegacyActive account state preserves old behavior. With matching original account and project heads, account Frozen/Preparing/Validated/Activated and verified WQ02 frozen-or-later state yield OldWriterFenced, before execution. Missing proof, changed head, phase mismatch, Aborted, bad protocol or wrong project binding yield OldWriterFenceUnknown, also denying the old writer. Neither an agent label, action name, boolean, operation UUID, nor a user-supplied migration phase grants an exception.

The two guarded entrypoints in Client/SignalR/Client.fs are before the authenticated SignalR command's implicit Ready-to-Working activation (including the nominally read-only AgentInbox) and at the common source-dispatch boundary used by direct local entrypoints. This also checks the phase again just before dispatch. Legacy Agent registration, task assignment and lifecycle, role/instruction attachment, review claim/decision, old integration submit/complete, challenges, pending legacy-intent replay, and new old Manager/ScheduleExecution (including AfterAgentIdle) are denied. WorkQueue commands and WQ04 native composition commands are not old Agent writes. Historical non-mutating reads remain available. Explicit Abandon/Supersede pending intent remain operator repair; Retry pending old intent cannot republish.

Implicit old Ready-to-Working activation and response-alert consumption are separately fenced at their side-effect sites, including when they are triggered by ordinary historical read commands. Those historical reads remain available without changing old Agent state. This is a command-pipeline guard, not a replacement for original Git transaction fencing.

## Local Release negative proof

Client.Tests/LegacyWriterProtocolFenceTests.fs uses disposable original project and account bare Git remotes, each with two separate clones. It seeds a historical persistent Agent, verifies actual legacy RegisterAgent while the account is LegacyActive, then synthesizes Frozen and Preparing on the disposable original account Git ref (no production signer or Freeze writer exists). For each client it exercises old registration, assignment, review/integration, Manager/AfterAgentIdle and AgentInbox negatives, proves the original action callback is not invoked and the original project ref is unchanged, and reads historical Agent evidence afterwards. Additional cases test stale account/project heads, malformed protocol, wrong project identity, host-selected binding checks, audit-only operator recovery and Work-native read allowance.

Run locally with .NET 11 Release:
- dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~LegacyWriterProtocolFenceTests
- dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~SignalRDispatchTests

## Important remaining gaps: no cutover GO

1. No operational Freeze signer, authenticated account-to-project participant roster, host-owned trust registry, or connected-client minimum protocol enforcement exists. A disconnected or older client without this default-off gate can still write. No production configuration is provided by this branch.
2. Direct library calls to ReviewWorkflow, ChallengeWorkflow, RoleInstructions and LedgerStore.transact, source scripts, and other old in-process write routes bypass the guarded authenticated command pipeline. Checking WQ07 and WQ02 Git heads before dispatch is not one atomic account-plus-project Git CAS. A late already-admitted old writer can still win; no global fence/journal high-water was established.
3. Previously saved browser/Manager schedules and background rearm/execute are outside the command-creation gate. Current AfterAgentIdle is separately inert because there is no durable activity proof; this is not proof of authenticated old Scheduler retirement.
4. AgentInbox is blocked because it writes. The guarded SignalR pipeline also suppresses implicit Agent activation and response-alert consumption on other historical reads. Direct callers of ReviewWorkflow.takeResponseAlerts and other old library side effects still bypass this boundary and require an original Git transaction fence at cutover.
5. Account Preparing and project Prepared positive writers, historical source journal completeness, cross-ledger partial reconciliation, Work-native host bootstrap, and operator GO are separately unproven. This branch does not launch agents, enable Scheduler, change any production migration phase, or permit cutover.

An independently Accepted review of these fixtures proves only their negative source boundary. **Operational cutover remains NO-GO.**
