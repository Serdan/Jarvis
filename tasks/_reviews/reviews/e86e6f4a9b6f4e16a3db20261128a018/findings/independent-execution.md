# Independent verification — notification retirement regression package

## Review identity and source
- Review request: `e86e6f4a-9b6f-4e16-a3db-20261128a018`
- Exact frozen source HEAD: `93b63cb336b24b3fdcc0049a89ee921416451672`
- Recorded base: `914907d14803ddf861e438419147903dcbf8f7f7`
- Reviewer worktree: `review-e86e6f4a-r7`, checked out at the exact frozen HEAD.
- Diff against recorded base contains exactly three additions: the preflight document, standalone test project, and F# test source. No production source or solution/test-workflow registration is in the diff.
- Test source/project-only `git diff --check` passed (job `66f60af644cb4647b5125d6925042744`, exit 0).

## Independent test execution ledger

### Green compatibility controls — independently verified
- Job ID: `105fa6077f9545dea44fecd952c1829d`
- Command: `dotnet test tests/NotificationRetirement20261127/NotificationRetirement.Tests.fsproj -c Release --filter 'TestCategory!=RetirementContract' --logger 'console;verbosity=normal'`
- Purpose: independently verify the four green compatibility controls.
- Source: exact frozen HEAD above, in the isolated review worktree.
- Terminal evidence: Completed, exit code 0; NUnit discovered 4/4 cases; all four passed, 0 failed; `Test Run Successful`; Release build produced Common, Client, and the standalone test assembly. Existing Client FS0760 and standalone-project FS0988 compiler warnings were non-fatal. This is a separate run from the red category.

### Intentional RED retirement contract — independent run pending
- Job ID: `54c24bcd8c7046c6b2a8f1a422034668`
- Command: `dotnet test tests/NotificationRetirement20261127/NotificationRetirement.Tests.fsproj -c Release --filter 'TestCategory=RetirementContract' --logger 'console;verbosity=normal'`
- Purpose: independently verify all six intentional red-first assertions, separately from green controls.
- Source: exact frozen HEAD above, in the same isolated review worktree.
- Required terminal evidence: Completed with exit code 1; NUnit discovers exactly 6/6 cases and each fails at its explicit `INTENTIONAL RED` assertion (not build, fixture, authentication, or infrastructure failure). Job is pending at this checkpoint; it must be reconciled once at terminal state.

### Diff hygiene finding
- Job `caa0379dcf824d42b549a560132fc908` ran `git diff --check 914907d14803ddf861e438419147903dcbf8f7f7 93b63cb336b24b3fdcc0049a89ee921416451672`; Completed, exit code 2.
- The sole report is `docs/reviews/notification-retirement-20261127/preflight.md:3`: trailing whitespace on the two spaces used for a Markdown hard line break after the task name.
- A repeat with `--ignore-space-at-eol` (job `aaeffa70755642e39a1e7738554c6708`) reported the same line. The two executable test-package files pass `git diff --check` separately. This is a documentation/hygiene discrepancy: the preflight's claim that package-wide `git diff --check` passed is not reproducible on this frozen commit. It is not a production or test-behavior defect.

## Isolation and safety assessment
The fixtures create disposable local bare Git repositories and clones, randomized project/ledger IDs, and a private Linux XDG config root. They set an empty global Git config and `GIT_CONFIG_NOSYSTEM=1`, assert that Jarvis config resolves inside the private root, and remove that root in `finally`. The registered-ingress case exercises the real local client command processor against fixture-bound checkouts; it does not use a server/network ingress or shared project registry. Direct workflow cross-project cases verify sender membership in a separate synthetic source ledger but do not claim to execute the server source-validation hop.

The source diff is limited to these three additions. The standalone test project is not in the solution or configured normal test workflow. No production source, server route, shared registration, production ledger, live notification, deployment, ACK, dismissal, or notification cleanup is changed or exercised. The package is a test-only preflight and does not implement retirement or authorize mutation of live notices.
