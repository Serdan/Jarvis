# Reviewer 3 — Independent review of Work-native credential launch binding

**Review ID:** `e2f9c3318b644f6a8d3084fb696a231d`  
**Pinned source:** `a161b03b066c7356a0bbe08b3a9371a647d5059f`  
**Base:** `dfb73853bac06e3076a4e546517a2a640f493418`  
**Verdict:** **Accepted for the default-off, disposable/offline adapter scope only.** **Operational HostBridgeUnavailable / NO-GO** remains explicit.

## Independently inspected

1. **Original credential admission and Git CAS.** `WorkQueueDurableLaunchIntent.admitPreparedCore` threads an issued, volatile credential to `WorkQueuePracticalAdmission.admitExplicitWithCredential`. The original WQ02/WQ04 admission planner writes project state, original admission, credential verifier and pinned role admission in **one four-path project Git CAS**, with the original WQ07 held-token and selected Work/source witness. No second bearer may replace an already-published verifier; a lost original acknowledgment retains the original ticket/operation. Legacy three-path replay cannot downgrade the credentialed admission. Verified `WorkQueueGitLedger.fs:1381-1466`, `WorkQueuePracticalAdmission.fs:214-320`, `WorkQueueDurableLaunchIntent.fs:459-565`.
2. **Private fake-provider delivery.** The only delivery is `NativePrivateBootstrap.TakeOnceFor` with a matching cooperative execution ID, one consumption per bootstrap. Issued credential/ticket/bootstrap representations are redacted. The accepted fake-provider reply must have consumed the private bootstrap and may not persist the bearer within its receipt; fixed strings, never provider exception details, are written on unknown outcomes. No production caller reaches these methods (`git grep` Client/Server/Client.Tests); production entrypoints immediately report `HostBridgeUnavailable`.
3. **Original WQ03 send journal.** `submitDisposable` verifies the original admission and create-only WQ11 Git host claim, commits the original SQLite/anchor one-shot marker **before** calling the fake provider, and forbids re-send on existing/uncertain/corrupt evidence. Unknown send is retained in the WQ03 journal and attempts WQ02 `OutcomeUnknown` with the original dispatch UUID; failure to publish that auxiliary witness still forbids a new send. Cross-host races are covered by the adjacent Git-host-claim fixture.
4. **First credential-authenticated connect and replay.** `firstConnectDisposable` requires the original accepted journal/host claim, unchanged original launch, role, account held-token and persisted verifier; `executeCredentialFixtureAt` checks the 256-bit bearer against SHA-256 verifier at preflight and winning Git CAS. Exact first-connect UUID replay is idempotent; wrong bearer, foreign session/launch/role and fresh post-STOP mutations are denied. New launch adapter does not call `registerAgent`; fixture explicitly compares `agents/` tree before/after the original launch.
5. **Secret handling.** The dynamic bearer is not placed into original Git records, the WQ03 browser intent, browser argument JSON, prompt, source ref or dispatch operation. Tests inspect stored credential/project records; source references of `RevealForPrivateDelivery` are limited to private bootstrap/receipt guard and test fixture. No live provider or relay path exists, so production logging/privacy remains untested and unapproved.

## Local independent results on the exact frozen source

Using SDK `11.0.100`, fresh local NuGet restore, `Release` configuration, real NUnit discovery (not a zero-output `--no-restore` shortcut):

- Native credential adapter + WQ02 credential + WQ12 durable intent + practical WQ04 admission: **17/17 passed**.
- Original WQ03 browser journal + WQ11 verified dispatch + native lifecycle CAS: **14/14 passed**.
- WQ release acceptance: **8/8 passed**.
- **Combined: 39/39 passed**, in three isolated test invocations.
- `git rev-parse HEAD` matched `a161b03b066c7356a0bbe08b3a9371a647d5059f`; `git status --porcelain --untracked-files=all` empty; `git diff --check` clean. No reviewer source edits, hosted GitHub Actions, Codex or live browser injection.

## Decision scope and gaps

**Accepted:** The frozen, opt-in offline fake-provider adapter is a fail-closed stepping stone to a Work-native launch without a newly persistent Agent entity. No blocking finding was established within that narrow source boundary.

**Not accepted or claimed:** Operational provider-secret injection, private first authenticated relay/tool connection, live WQ03 host reconciliation, Scheduler activation, public mutating authority, role/source/account gate cutover, production migration, deployment or legacy-Agent retirement. The execution-ID check is cooperative, not host attestation. A real private HostBridge is absent and production entrypoints return `HostBridgeUnavailable`. See `findings/host-bridge-unavailable.md`. Lead-only integration of this exact reviewed source remains a separate decision.
