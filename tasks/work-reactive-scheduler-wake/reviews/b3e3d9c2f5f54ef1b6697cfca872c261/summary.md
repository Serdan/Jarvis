# Reviewer 1 — changes requested

**Frozen source:** `b3e3d9c2f5f5fef1b6697cfca872c26177d1d6d1` (base `944bca7fa38cb5e973b04ce7829966cca351202b`). Five changed files, scoped to the reactive coordinator and tests; no production scheduler registration or real browser dispatch introduced.

**Release verification:** `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueReactiveWake --verbosity minimal` passed **9/9** on .NET 11 at the frozen worktree. Full suite was not verified in this review; submitter reports a 120-second timeout without test-failure evidence.

**Findings (see linked findings files):**

1. **P1 — bound reader blocks subsequent legitimate Work after a normal prior launch.** `boundRead` uses the no-original-journal preview; that preview flags `BrowserJournalUnknown` for each historical launch, including terminal/released history. `blockingIssues` treats this as a global stop. After one launch exists, future independent Work can no longer wake despite released capacity. Preserve fail-closed treatment of unresolved effects, but distinguish proven settled history and regression-test the successor Work case with disposable Git fixtures.
2. **P2 — same-thread reentrant wake can double-invoke the executor.** `Execute` runs under a reentrant monitor before recording the original operation ID as in-flight/recent. A synchronous event callback into `Wake` recursively invokes the same executor for the same intent. Mark in-flight before external callbacks, retain durable replay rules, and test nested synchronous signals.

**Disposition:** Request changes. Acceptance should cover a verified terminal+released historic launch followed by new eligible Work; an unresolved original operation must still fail closed, and reentrant wake must never duplicate callback effects. No lead integration until a corrected frozen review is accepted.