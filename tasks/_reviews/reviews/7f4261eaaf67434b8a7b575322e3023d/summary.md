# Independent review summary

## Exact source and scope

Reviewed frozen commit `084376cc80464a028c01cf5a285677af0f1f744d` (tree `8ce268c4983898ea63284b899f79ca54ad97e2a0`) in the clean registered `challenge-role-policy-w12-20261127` worktree. Its actual parent is `175ad976a6226f1cb07901b4b43f3d6b0ecf3de1`, which is also the observed `main` HEAD. The exact diff against that parent contains only three added files: `Client/Modules/ChallengeRolePolicy.fs`, `tests/ChallengeRolePolicy20261127/ChallengeRolePolicy20261127.fsproj`, and `tests/ChallengeRolePolicy20261127/PolicyTests.fs`. `git diff --check` exited 0; post-test `git status` is clean.

Provenance caveat: the review request's `BaseCommit` field says `175ad976a6226f1cb07901cf5a285677af0f1f744d`, which is not present as a commit in the source worktree (`git rev-parse` reports unknown revision). I therefore inspected the diff against the frozen commit's actual parent, which is independently confirmed as the target's observed `main` HEAD. The source ancestry is clear, but the request metadata is inconsistent and should not be treated as the actual diff base.

## Validation

The author's submitted evidence records red-first qualification: controls 2/2 pass and the intended 6/6 assertions fail against the deny-all baseline (checkpoint 20323); the original lost attempt was not replayed. The author also records an exact green .NET 11 Release run (job `14f549f119c74b4dafc59309eda03287`, exit 0, 8 passed, 0 failed, 0 skipped).

Reviewer 7 independently ran the exact frozen source:
- Debug: job `1f4eb08b896a48b5acbee743a105c8a4`, exit 0, 8 passed, 0 failed, 0 skipped.
- Release: job `23364d46250046adbbad3ed6c1db00f1`, exit 0, 8 passed, 0 failed, 0 skipped.

Both runs emit `FS0988` (empty main module) as a warning; it does not fail the NUnit test assembly. No production wiring, registered-route enforcement, canonical role configuration, migration, live grant, publication, integration, or deployment is claimed or authorized by this Stage B review.

## Assessment

The isolated module provides generic directed role edges, typed denials, fail-closed handling for absent/invalid policy and missing/mixed/unknown/project-mismatched principals, all-target admission, dispatcher-plus-original-issuer intersection, and a distinct trusted-human response value. The fixture covers the main positive/negative matrix and explicit configuration changes.

One in-scope defect remains: the default graph includes `Health` and `Manager` as ordinary roles and grants each an edge to `Lead`. Canonical role instructions explicitly retire legacy LLM Health and Manager; the committed fixture also expects these retired roles to be authorized. See `findings/default-policy-includes-retired-roles.md` for reproduction, requirement, impact, and bounded correction.

## Verdict

**ChangesRequested** for the retired-role default allow-list defect only. Preserve the Stage B boundary: correct the pure policy and fixture, then submit a new exact-source review. Do not expand this correction into production wiring, migration, live configuration, or deployment.