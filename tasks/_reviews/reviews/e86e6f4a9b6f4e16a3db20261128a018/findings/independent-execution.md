# Independent verification — notification retirement regression package

## Review identity and source
- Review request: `e86e6f4a-9b6f-4e16-a3db-20261128a018`
- Exact frozen source HEAD: `93b63cb336b24b3fdcc0049a89ee921416451672`
- Recorded base: `914907d14803ddf861e438419147903dcbf8f7f7`
- Reviewer worktree: `review-e86e6f4a-r7`, checked out at the exact frozen HEAD.
- Diff against recorded base contains exactly three additions: the preflight document, standalone test project, and F# test source. No production source or solution/test-workflow registration is in the diff.
- Test source/project-only `git diff --check` passed (job `66f60af644cb4647b5125d6925042744`, exit 0).
- `git grep -n NotificationRetirement20261127 <head> -- '*.slnx' '*.sln' '*.json'` returned no matches (job `b474a2d980684e40ac0462744d20893d`, exit 1/no matches). The standalone test project is not registered in those normal-workflow files.
- Post-test worktree status is clean (branch only; no tracked or untracked changes reported).

## Independent test execution ledger

### Green compatibility controls — independently verified
- Job ID: `105fa6077f9545dea44fecd952c1829d`
- Command: `dotnet test tests/NotificationRetirement20261127/NotificationRetirement.Tests.fsproj -c Release --filter 'TestCategory!=RetirementContract' --logger 'console;verbosity=normal'`
- Purpose: independently verify the four green compatibility controls.
- Source: exact frozen HEAD above, in the isolated review worktree.
- Terminal evidence: Completed, exit code 0; NUnit discovered 4/4 cases; all four passed, 0 failed; `Test Run Successful`; Release build produced Common, Client, and the standalone test assembly. The four cases were `BlockedRetrySurvivesPreferenceChangesAndStopRemainsIndependent`, `CrossProjectSourceIdentityAndRecipientAuthority`, `DeliveredRetryAndRecipientAckPreserveHistory`, and `ForeignRouteAndRecipientAckUseActualClientIngress`.
- Compiler warnings were non-fatal: Client FS0760 and F# FS0988 (`Main module of program is empty`) from the standalone test source. FS0988 is an additional warning not listed in the submitted preflight's statement that FS0760 was the only compiler output.

### Intentional RED retirement contract — independently verified
- Job ID: `54c24bcd8c7046c6b2a8f1a422034668`
- Command: `dotnet test tests/NotificationRetirement20261127/NotificationRetirement.Tests.fsproj -c Release --filter 'TestCategory=RetirementContract' --logger 'console;verbosity=normal'`
- Purpose: independently verify all six intentional red-first assertions, separately from green controls.
- Source: exact frozen HEAD above, in the same isolated review worktree.
- Terminal evidence: Completed, exit code 1; NUnit discovered exactly 6/6 cases and all six failed at their explicit `INTENTIONAL RED` assertions. The Release build and test discovery succeeded; no fixture, authentication, or infrastructure failure occurred.
- Exact observed outcomes:
  1. `RegisteredFreshSendMustReject(False)`: fresh same-project registered send succeeded; ledger changed; one pending notice.
  2. `RegisteredFreshSendMustReject(True)`: fresh cross-project registered send succeeded; ledger changed; one pending notice.
  3. `FreshSendMustRejectWithoutLedgerMutation("", false)`: fresh same-project send returned Delivered; ledger changed; one pending notice.
  4. `FreshSendMustRejectWithoutLedgerMutation("", true)`: fresh same-project muted send returned Blocked; ledger changed; zero pending notices (suppression receipt).
  5. `FreshSendMustRejectWithoutLedgerMutation("foreign", false)`: fresh cross-project send returned Delivered; ledger changed; one pending notice.
  6. `FreshSendMustRejectWithoutLedgerMutation("foreign", true)`: fresh cross-project muted send returned Blocked; ledger changed; zero pending notices (suppression receipt).
- The expected RED therefore demonstrates the existing legacy fresh-operation behavior, including the otherwise-hidden suppression write. The run was not repeated.

### Diff hygiene finding
- Job `caa0379dcf824d42b549a560132fc908` ran `git diff --check 914907d14803ddf861e438419147903dcbf8f7f7 93b63cb336b24b3fdcc0049a89ee921416451672`; Completed, exit code 2.
- The sole report is `docs/reviews/notification-retirement-20261127/preflight.md:3`: trailing whitespace on the two spaces used for a Markdown hard line break after the task name.
- A repeat with `--ignore-space-at-eol` (job `aaeffa70755642e39a1e7738554c6708`) reported the same line. The two executable test-package files pass `git diff --check` separately. This is a documentation/hygiene discrepancy: the preflight's claim that package-wide `git diff --check` passed is not reproducible on this frozen commit. It is not a production or test-behavior defect.

## Isolation and safety assessment
The fixtures create disposable local bare Git repositories and clones, randomized project/ledger IDs, and a private Linux XDG config root. They set an empty global Git config and `GIT_CONFIG_NOSYSTEM=1`, assert that Jarvis config resolves inside the private root, and remove that root in `finally`. The registered-ingress case exercises the real local client command processor against fixture-bound checkouts; it does not use a server/network ingress or shared project registry. Direct workflow cross-project cases verify sender membership in a separate synthetic source ledger but do not claim to execute the server source-validation hop.

The source diff is limited to these three additions. The standalone test project is not in the solution or configured normal test workflow. No production source, server route, shared registration, production ledger, live notification, deployment, ACK, dismissal, or notification cleanup is changed or exercised. The package is a test-only preflight and does not implement retirement or authorize mutation of live notices.

## Decision basis
The requested 4/4 green controls and 6/6 intentional RED assertions are independently reproduced on the exact frozen commit. All six red cases fail for the intended legacy mutation/suppression outcomes; all green compatibility controls pass. Isolation is credible from source inspection and the fixture setup, and the package diff contains no production mutation or live-cleanup operation. The two preflight documentation inaccuracies (one Markdown trailing-space report and one additional non-fatal compiler warning) are low-severity evidence-hygiene issues, not blockers to this test-only package.
