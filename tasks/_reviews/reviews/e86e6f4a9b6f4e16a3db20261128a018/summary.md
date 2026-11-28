# Independent review summary

**Decision: Accept.** The test-only notification-retirement regression package is valid for its stated red-first preflight purpose on exact frozen HEAD `93b63cb336b24b3fdcc0049a89ee921416451672`.

## Verified evidence
- Green compatibility controls: independently ran the Release test command; NUnit discovered 4/4 and all 4 passed (job `105fa6077f9545dea44fecd952c1829d`, exit 0).
- Intentional RED contract: independently ran the separate Release filter; NUnit discovered 6/6 and all 6 failed at the explicit `INTENTIONAL RED` assertions (job `54c24bcd8c7046c6b2a8f1a422034668`, exit 1). The six observed legacy outcomes are detailed in `findings/independent-execution.md`.
- The package was committed at `0a7a6944c698e20e58315c8b6f05d52fdfc8a97e`; the exact reviewed HEAD `93b63cb336b24b3fdcc0049a89ee921416451672` is the documentation-only finalization commit. Independent `git diff --name-status 0a7a..93b63` confirms it modifies only `docs/reviews/notification-retirement-20261127/preflight.md`. Relative to the recorded base `914907d14803ddf861e438419147903dcbf8f7f7`, the complete package consists of exactly three additions: that preflight document, the standalone test project, and the F# test source.
- No production source or server route is changed. The standalone project is not registered in solution or JSON workflow files, and the post-test reviewer worktree is clean.

## Findings and limitations
- The fixtures use disposable local bare Git repositories/clones, randomized IDs, and a private temporary XDG/Git configuration root; they remove the fixture root in `finally`. No shared project registration, production ledger, live notification, deployment, ACK, dismissal, or cleanup is performed.
- One green compatibility test exercises the actual local client command processor against fixture-bound checkouts. This is not a server/network-ingress test. Direct cross-project workflow cases check synthetic source-ledger membership but do not claim to test the server source-validation hop.
- Non-blocking documentation discrepancies: package-wide `git diff --check` reports the two trailing spaces used for a Markdown hard line break in preflight.md line 3, despite the document saying the check passed; the independent build also emitted a non-fatal FS0988 warning in addition to FS0760. The executable test files themselves pass `git diff --check`.

These discrepancies do not undermine the 4/4 green controls, the 6/6 expected legacy RED assertions, or the test-only isolation boundary. The package does not implement retirement and must not be interpreted as permission to mutate or clear live notifications. The parent production task and its separate source lease remain authoritative.
