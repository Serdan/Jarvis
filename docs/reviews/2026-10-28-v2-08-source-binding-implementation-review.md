# V2-08 source repository/worktree provenance — implementation review

## Authority model

The assignment belongs solely to the agent/attempt in the orphan Git ledger.
The checkout name, path, working branch and HEAD are observations only; deleting
or moving a worktree does not change the attempt or release the Working slot.

`BindAttemptSource` pins the selected Working attempt, assignment generation,
base commit, target ref, fully qualified implementation source ref, and
source-repository identity. The independent source identity is a versioned
SHA-256 fingerprint of the **approved Git fetch and push endpoints** (not of the
V2 ledger project UUID). The client checks its configured endpoint(s) against
the approved binding and requires a registered Git worktree. The source branch
must contain the base; the base must also be an ancestor of the freshly fetched
approved remote target. A missing or malformed source branch fails closed.

`InspectAttemptSource` reads the ledger-bound identity and returns the current
local source ref and registered worktree state, including `SourceUnavailable`,
`AvailableNoWorktree`, `CheckedOut`, `DetachedCheckout`, and `Uncommitted`.
Observation is read-only; absence never mutates ownership or marks the agent
Ready/Completed/Parked/Free.

`RebindAttemptSource` is an explicit idempotent, version- and
generation-guarded branch-ref update. It preserves owner, base, target,
repository identity, and previously frozen review revision evidence. Detached
and uncommitted selected checkouts require caller acknowledgement.

Submission and replacement verify the approved repository identity before
publishing the immutable source review ref. Frozen review claims/decisions and
source integration verify the repository ID and both endpoints independently,
but do not require historical working source branches still to exist.
Integration evidence records the same repository identity and endpoints and
cannot silently switch to a different push endpoint.

## Limitations and deferred questions

- Git itself has no standardized, immutable remote-repository UUID. This
  fingerprint identifies **approved endpoint configuration**, not a
  cryptographically attested hosting-provider repository. Trust still depends
  on the explicit V2 Git remote approval and preserved ledger history.
  An operator-authorized change to the source remote identity requires a
  separate explicit rebind/recovery design; it is never implicitly accepted.
- Local worktree observation is not replicated as authoritative ledger state.
  Clients on other machines may see an unavailable local worktree even while
  immutable source review refs remain fetchable remotely.
- Initial assignment issue can predate source selection, so its source
  fields are blank; subsequent issued generations capture the source fields
  present at their issuance. Source binding itself is recorded as a versioned
  canonical ledger mutation and immutable operation receipt.
- A remote branch can change after a read-only observation. Every subsequent
  write must re-verify its own input and enforce the ledger CAS; review heads
  are pinned by exact Git object ID, not a moving branch tip.

## Verification

Disposable Git remotes and registered worktrees cover:
- Cross-clone identity parity with an unrelated ledger project UUID
- Raw filesystem worktree move (prunable registration), Git repair, normal removal; branch deletion and recreation
- Agent remains Working with its occupied slot while source disappears
- Checkout move/recreation and branch rename via explicit rebind
- Pre-existing claimed revision remains immutable and reviewable after rename
- Detached and dirty worktrees require explicit opt-in; stale detached HEAD is rejected even with opt-in
- Changed local push endpoint and a mismatched approved endpoint are rejected
- Existing immutable submission/decision evidence records approved source
  repository identity and both endpoints
- Explicit protocol commands, permissions and pending-operation identity

## Test commands and protocol

- `dotnet test Client.Tests/Client.Tests.fsproj --no-restore -v:q`:
  336/336 tests passed after the independent identity correction; targeted
  V2-08 branch move/deletion/repair, source rebind, dirty and stale detached
  assertions passed on the final candidate.
- `dotnet test Server.Tests/Server.Tests.fsproj --no-restore -v:q`:
  62/62 passed with opt-in command metadata and scopes validated.
- Experimental client/server protocol bumped from 3.30 to 3.31 because
  `V2TaskWriteCommand` now carries source-ref and checkout acknowledgment fields.
  No deployment was attempted.

This milestone is limited to V2 isolated source/worktree APIs and does not
authorize deployment, V1 migration, a new server database or source Git edits
on behalf of the caller.
