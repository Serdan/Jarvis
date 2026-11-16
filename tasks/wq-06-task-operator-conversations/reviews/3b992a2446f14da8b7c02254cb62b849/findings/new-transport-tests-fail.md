# P1 — New transport tests make the default Release test suite fail

**Frozen commit:** `e22675efb846e6bb86106bf6dc05a6bdfe2e0d5a`.

**Location:** `Client.Tests/Client.Tests.fsproj:40-41` includes `WorkQueueConversationTransportTests.fs`; fixture setup at `Client.Tests/WorkQueueConversationTransportTests.fs:93,108,186,223,263`.

**Reproduction (frozen WQ06 worktree, .NET 11):**
```bash
dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueConversationTransportTests --no-restore --no-build
```
Exit code 1: **0 passed, 4 failed**. Three tests fail when fixture `PublishStage` encounters the unchanged WQ04 canonical-producer guard, and the fourth fails when fixture `PostMessage(RoleMessage)` lacks canonical message origin / pinned role authority. These are normal fail-closed rejections, not evidence of a broken production guard.

The new file was added to the default `Client.Tests.fsproj` in this change. Consequently a normal full Release run newly includes four guaranteed failures. The change's own documentation acknowledges this, but leaving known-red tests in the default suite is a real regression that obscures unrelated failures and prevents meaningful green validation.

**Requested change:** Keep the guards intact. Either update the fixtures to construct genuinely authorized WQ04 producer/role evidence, or keep these still-unintegrated transport tests out of the default runnable suite (with an explicit pending reason) until their prerequisite integration. Re-run the relevant Release suite and record green results.

The separate manual operator tests **do** pass independently: `FullyQualifiedName~WorkQueueOperatorConversationsTests` = 5 passed, 0 failed. This finding does not object to the manual Task/Role Git-CAS operator path itself.
