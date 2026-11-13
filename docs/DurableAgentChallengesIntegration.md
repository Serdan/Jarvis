# Durable agent challenges: integration handoff

This feature is intentionally **not merged or deployed**. Review the independent source branch `feature/durable-agent-challenges` before integrating it into Jarvis `main`.

## Baseline and merge preview

As observed on 2026-11-13, `main` pointed at `4dc2b489`. A read-only `git merge-tree --write-tree --messages main feature/durable-agent-challenges` found nine content conflicts. The branch had diverged from main by 56 main-side commits and 12 feature-side commits. Re-run the preview against the latest heads; these counts will change.

| Conflicted file | Resolution intent |
| --- | --- |
| `Common/Messages/AgentCommand.fs` | Preserve all main-side job, browser and role protocol changes, then add `ChallengeRead`/`ChallengeWrite` wire cases and DTOs. |
| `Client/SignalR/Client.fs` | Preserve current async job/dispatch behavior and browser routing, then add typed challenge dispatch without changing existing tools. |
| `Client/Modules/LedgerStore.fs` | Preserve latest verified ledger and source identity safeguards; retain challenge entity support and immutable dependency indexes. |
| `Client/Modules/PermissionPolicy.fs` | Union the current main permission catalog with challenge read/write access. |
| `Server/McpTools.fs` | Retain current MCP capacity/dispatch and catalog behavior; add challenge tools including subscription, CAS assignment and split. |
| `Server/Server.fsproj` | Retain current .NET 11 target, package versions and main-side service files; do not restore obsolete .NET 10 pins. |
| `Client.Tests/ReviewWorkflowTests.fs` | Preserve new main-side agent and role tests and feature blocked-task coverage. |
| `Server.Tests/McpAuthenticationTests.fs` | Reconcile the list of mutating MCP methods and their optional operation-ID contract. |
| `Server.Tests/ServiceTests.fs` | Reconcile the tool catalog, wire aliases, OAuth metadata, and per-command device-ID assertions. |

`Client/Modules/ReviewWorkflow.fs`, `Client/Modules/OrchestrationApi.fs`, and the project test manifest auto-merged in this preview, but still require a semantic review and test run: passing text merges do not prove correct interaction with newer main code.

## Correctness invariants to preserve

- Challenge conditions are project-scoped, durable, explicit, version-pinned and authorization-checked; neither challenge transitions nor dependent task wakeups emit notifications.
- A challenge never blocks its issuer by default. Explicit blocked attempts link up to 16 dependencies; final ALL satisfaction wakes only linked work.
- An available owner receives a new `Ready` assignment (never `Working`), and a busy or Parked owner keeps durable `Eligible` work without preemption.
- The owner-slot release paths—including completion, administrative release, unpark, and blocking another task—reconcile eligible work.
- Already-completed-only links must be rejected at block time: they cannot produce a future wakeup event. Mixed completed and unfinished links remain valid.
- Immutable `challenge-waits/by-challenge/...` and `challenge-waits/by-owner/...` reverse indexes avoid scanning historical task records; candidates must be revalidated against canonical task, attempt, owner, wait status and generation.
- Condition assignment and splitting use exact challenge version CAS, preserve stable IDs and immutable history, and do not alter task ownership.
- The self-inbox remains bounded, relevant and snapshot-consistent; observing another agent must not activate their execution.

## Isolated staging reconciliation (2026-11-13)

The reviewed `c5a839d6` source was reconciled against `main` at `b24d4358` on a **separate** `integration/durable-agent-challenges-net11` worktree. The nine conflicts above have been explicitly resolved there without changing the source feature, `main`, or deployed services. This candidate is not an approved production integration.

- Preserve cross-project forwarded notifications and add ChallengeRead/Write cases throughout command naming, routing, OAuth and permission policy.
- Preserve existing Git ledger role enumeration while allowing tightly scoped challenge and reverse-dependency index reads; do not widen arbitrary path enumeration.
- Preserve main's stricter `FS0025` match-exhaustiveness compilation guard and .NET 11 configuration. Set the combined wire protocol version to `3.66`.
- Keep independent main-side dashboard/trust-anchor regression tests *and* feature-side challenge observational-read tests.
- The combined MCP method catalog contains nine additional client-backed aliases beyond either single-side formula; reflect the union in `ServiceTests` (`commandDefinitions.Length - 1 + 69`). The combined optional-operation-ID contract enumerates 49 mutating tools.
- Focused integrated ChallengeTests: **23/23 passed**; complete integrated Client.Tests: **634/634 passed**; complete integrated Server.Tests: **124/124 passed**. All were executed on .NET 11 Release before the staging branch was frozen.

## Verification and approval

Run `dotnet test Client.Tests/Client.Tests.fsproj -c Release` and `dotnet test Server.Tests/Server.Tests.fsproj -c Release` against the integrated .NET 11 tree. Also test multi-client competing condition resolution, stale wakeups, multiple dependents, busy/Parked owner releases, zero-notification paths, and MCP metadata coverage.

Request independent review on the exact pinned feature commit before any main-branch integration. This note does not authorize merging or deployment. Persistent Lead-role instructions for subscription and triage are in `docs/DurableAgentChallenges.md` and should be applied when that role is published.
