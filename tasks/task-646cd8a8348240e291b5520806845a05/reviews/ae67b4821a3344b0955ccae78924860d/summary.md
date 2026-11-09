# P1 pending-intent recovery — source review

**Reviewed commit:** `f6685b30fbc6ad3f940b429c4fb7d0dfcd0a7d40`  
**Target:** `main`  
**Verdict:** Accept for source integration. No client or server deployment authorized.

## Verification
- All 522 Client.Tests and 122 Server.Tests pass in Release, no skipped or failed tests.
- `git diff --check origin/main...HEAD` is clean.
- `git merge-tree --write-tree HEAD origin/main` succeeds without conflict. The feature is nine commits ahead and zero commits behind main at review.
- Code review checked canonical snapshot reuse for original operation UUID and entity state, task/review obsolescence proof restrictions, archived UUID tombstone/durability order, exact hash/head preconditions, operator approval and absence of normal public `resume_agent`.

## Regression findings
- Legacy ResumeAgent with pinned generation: same generation refused, newer canonical generation accepted for explicit abandonment, stale head/hash refused, wildcard generation refused. The original session-bearing command is archived immutably.
- Withdrawn branch review: stale Claim is refused before withdrawal; after canonical withdrawal the archived proof cites observed status/version without relying on a separate source ref read.
- Archive retry is idempotent and rejects changed proof. Late UUID identity mismatch remains visible without destroying audit. Separate supersession tests cover matching late acceptance and mismatched UUIDs.
- Summary and paged inspection preserve uncertain absence rather than upgrading it to proof of failure.

## Residual boundaries
- Tests executed on Linux; the Windows-native archive move and OS sharing behavior were not separately executed on Windows.
- No live pending intents were retired or replayed. Operator authorization is still required for any recovery action.
- No deployment is included in this source review.

**Blocking findings:** None observed.
