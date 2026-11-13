# P2 — Cancelled challenges continue to surface as actionable subscribed and assigned inbox work

**Frozen source:** `424fcfbb0a498e3f2134bf50260cf163f59c82fb`, `feature/durable-agent-challenges`, base `ecb59e2272c7e6c4e127345f5034695c2f6e5465`, review `8ebad7d7904449ac920302c24b7d7476`.

## Source path

- `Client/Modules/ChallengeWorkflow.fs:456–544`: `cancel` sets overall `AgentChallenge.Status` to `Cancelled`, while leaving still-pending condition records as `Status="Pending"`. Preserving condition history and avoiding fabricated satisfaction is correct.
- `Client/Modules/ChallengeWorkflow.fs:751–764`: `enrichInboxAt` populates `ChallengeAssigned` for **every condition with `OwnerAgentId=actor && Status="Pending"`** and `ChallengeTriage` for **every unassigned Pending condition when the actor is subscribed**, but does **not check the enclosing `challenge.Status`**. Thus a terminal Cancelled challenge continues to appear in both actionable lists, regardless of which actor cancelled it or when.
- `Client/Modules/ChallengeWorkflow.fs:566–574` `assignCondition` explicitly rejects a cancelled challenge as stale; `update` similarly rejects resolution/reopening after cancellation. The inbox therefore advertises work that can never be assigned or completed. The stale rows persist until other ledger state changes, but no legal transition removes the Pending condition from the cancelled challenge.
- `get_challenge` and paged history should continue to show cancellation for audit. This finding applies to the **actionable** self-inbox lists, not to historical visibility, and does not require sending notifications.

## Independent frozen-source negative reproduction

Created isolated reviewer-only worktree `reviewer1-challenge-cancel-inbox` from exact HEAD `424fcfbb...` and appended `ChallengeWorkflowTests.CancelledChallengeConditionsDoNotRemainActionableInInbox` to its test file (no owner source changes). The NUnit fixture creates one unassigned condition and one `Lead`-assigned condition, subscribes Lead, verifies both initially appear in `ChallengeTriage` / `ChallengeAssigned`, cancels both as issuer, and re-reads the **snapshot-consistent production `ReviewWorkflow.getAgentInboxAt |> ChallengeWorkflow.enrichInboxAt`** projection.

`dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~CancelledChallengeConditionsDoNotRemainActionableInInbox` **failed 1/1, exit 1**, job `3215c4f90a01490caa60a7eb00ae17b0`; at `ChallengeWorkflowTests.fs:937`, expected cancelled unassigned condition **absent** from actionable `ChallengeTriage`, but it was **present**. The assigned-condition assertion follows in the fixture but did not run after the first failure; the same unguarded condition-only predicate independently establishes its identical issue.

**Independent positive checks of untouched frozen branch:** focused ChallengeTests .NET 11 Release **21/21 pass**, job `a7201517c42d4c309dfb732bf08a0257`; full Server .NET 11 Release **127/127 pass**, job `c4438ec48d0b4105b3a76eef7efea53d`. Exact HEAD `424fcfbb...`, clean feature worktree, `git diff --check ecb59e2..424fcfbb` exit 0. These existing tests omit cancellation followed by inbox projection. No full Client suite independently rerun.

## Required correction

Derive the actionable assigned/triage projection from **both** condition and challenge states. Pending conditions belonging to `Cancelled` (or any future terminal) challenges must not be offered as actionable owner/dispatcher work. Preserve canonical cancellation and evidence/history for `get_challenge` and broader historical list, and preserve issuer-facing resolution/cancellation summaries as appropriate without inventing notifications. Add regression proving a subscribed dispatcher and an assigned owner both lose cancelled conditions from actionable inbox buckets, including snapshot consistency, no notifications and unchanged audit history.

**Severity P2 — blocks this review.** This violates the compact inbox promise that agents can act directly on the displayed assigned/unassigned conditions without a mandatory separate poll. It is an incorrect user-visible actionable state, not a cosmetic formatting issue.

No frozen branch edits, merge, deployment or production state mutation by Reviewer 1.
