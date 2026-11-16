# Reviewer 5 — changes requested

**Frozen commit:** `9eaa4e353c3ebc8a6df664ea5e23a9ccfb550dbb` (WQ11, five scoped files). This is an independent review of the default-off verified Work admission and injected fake original-dispatch adapter.

**Verified positive evidence:** The module is not wired to production Scheduler, timers or browser transport. The new path independently checks canonical project, WQ04 current role/source, account cap and admission evidence, and uses the WQ03 local durable marker before calling the injected provider. Replay of the same launch against the **same** journal correctly avoids re-sending; unknown/refused outcomes retain WQ07 capacity, with the pre-registration no-effect terminal gap explicitly documented. I independently ran the exact .NET 11 Release `WorkQueueVerifiedDispatchAdapterTests` fixture: **3/3 passed**. The tests use disposable Git authorities and no live browser dispatch.

**Blocking finding:** [P1] `findings/cross-host-one-shot.md`: two independent clients using *different* trusted journal roots can race through the same idempotently admitted launch and each obtain a local `OriginalMarkResult.Marked`, invoking the same original callback twice. The account/project CAS ensures one capacity token and one launch, but not one cross-host original provider call. Existing tests share one root and do not cover this negative-space race.

**Required before acceptance:** Make original-provider send authority single-valued across clients (enforced host binding or durable canonical pre-send fencing); fail closed on a foreign/unknown journal host; add a concurrent two-root same-launch regression. If this slice intentionally supports only one shared trusted host, enforce and explicitly state that invariant. This is a fake-only contract finding, **not** a claim that live browser sending currently occurs.

**Verdict:** RequestChanges. No production integration or code changes performed by reviewer.
