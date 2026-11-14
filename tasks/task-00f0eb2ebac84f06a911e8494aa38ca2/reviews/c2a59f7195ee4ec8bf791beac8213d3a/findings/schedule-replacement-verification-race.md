# P1 — Verification is reused across schedule replacement (confirmed regression)

Frozen source: `5afad770e0d7cd237d8c7d9f9d044674951a990f`.

**Location:** `Client/Modules/BrowserSchedules.fs:570-659`, specifically the lookup `actions |> List.tryFind (fun a -> a.Id = prior.Id)` at line 593 after awaiting verification.

The tick captures candidate schedule A, awaits the server's activity verification for A, then reacquires `gate` and looks up only `prior.Id`. `ScheduleExecution` can replace that ID while verification is pending. The tick consequently applies A's verified watermark to the entirely different current schedule B and may immediately claim and execute B without ever obtaining B's authoritative activity.

**Reproduction on the frozen commit:** In an isolated reviewer worktree, a deterministic NUnit test started an `AfterAgentIdle` schedule for agent A, blocked its verifier at the due tick, replaced the same schedule ID with a due schedule for B (B's authoritative remote activity is current), then released A's verification with `Ok None`. The scheduling loop launched B once. The expected launch count was zero; NUnit reported `Expected: 0; But was: 1` (`Client.Tests/BrowserSchedulesTests.fs` temporary reviewer test, at line 541; Jarvis job `d9ed91215b1b454e989e2473b1ce5c02`, exit 1). The temporary test was removed after reproduction; the frozen source and worker worktree were never modified.

**Required correction:** Give schedule entries immutable revision identities and verify that the exact candidate still exists with unchanged project, trigger, idle agent and relevant deadline after the awaited remote read; otherwise discard the result and reverify. Add a deterministic replacement-during-verification regression test, including a waiting schedule replacement case.

**Review status:** Blocking P1: violates cross-device idle safety and fresh verification for every launch.