# Actual manual capacity bypass witness

Pinned clean remote main: `82c534ad85c0aafcc2cd1b673f36dbca191d1826`, descendant of integrated `7a9ffa965b13f95a071cb125edbaa24cbe76f320`.
Task: `automation-final-manual-bypass-witness-20261125`.

The existing fixed-joint fixture reaches the fake browser and refuses its open request at capacity. This separate test-only fixture completes the genuine manual command instead. It uses the existing disposable Git-ledger fixture, a Working caller, an eligible Ready target, the actual launch template and checked-in launch script, an isolated local client, and the production BrowserBridge protocol over a loopback fake extension. No browser or provider is contacted.

Two cases start from an otherwise eligible Ready target and a real total cap of one occupied by the Working caller. The real Scheduler's same-snapshot preparation is held. The unavailable-data case additionally replaces ONLY the disposable capacity record with malformed JSON shape, proving both the production capacity reader and Scheduler preparation fail closed; genuine absence would mean disabled and is not used as an unavailable-data surrogate. Binding, target identity and ledger attribution remain available.

The test invokes `Client.SignalR.Client.processIncomingCommand` with `BrowserRunInNewTabCommand`, explicit target and fixture actor. Missing and unregistered target identity are rejected without browser frames or launch records. A valid manual invocation in each case publishes a real attributed intent before fake openTab, sends exactly the checked-in script and matching prompt/start URL, receives one fake submitted response, returns success, and persists the matching attributed outcome and script digest. It checks that no scheduled-ready intent exists, the Ready target's state/version is unchanged, and no second browser frame is observed during a bounded 250 ms interval after completion. The tab is deliberately retained by an explicit fixture-only command option to isolate the submission contract from cleanup.

Evidence boundary: the direct client dispatcher is the real manual command route after transport identity is supplied, not a new end-to-end JWT/server/provider test. Existing fixed-joint coverage handles authenticated ingress and scheduled STOP/Challenge/Health behavior. Neither this fixture nor a fake submission proves a live provider acknowledgement, actual external send, durable external conversation identity, cross-invocation idempotence, deployment, activation, or process restart. This test intentionally makes no production edits or changes to ordinary auth, identity, eligibility or STOP behavior; unavailable *capacity* data does not waive ordinary attribution or ledger integrity requirements.

## Scoped Release evidence

Fresh isolated dependency build and first test compile: job `fbeb705270874d58a2f0f6765b8b688c` built production Common and Client successfully, then failed on fixture-only nested F# module imports. Corrected those imports; the exact executable test source below passed both cases with no rebuild of production references (they were built in the same pinned checkout).

```sh
dotnet test tests/AutomationManualBypassWitness20261125/AutomationManualBypassWitness20261125.fsproj -c Release
# Exact passing incremental invocation:
dotnet test tests/AutomationManualBypassWitness20261125/AutomationManualBypassWitness20261125.fsproj -c Release --no-restore -p:BuildProjectReferences=false --logger 'trx;LogFileName=manual-bypass.trx' --results-directory /tmp/jarvis-w23-manual-bypass-20261125
```

Job `9da8f90aa6eb445c9dd2103921b8eb16`, terminal exit 0; 2 passed, 0 failed, 0 skipped, 5 s.

SHA256:
- Tests.fs: `c99b33a899bd5acd3dd0fb17703b9636ecc5ba7e744c5957f079389b240a0a7e`
- Client.Tests.dll: `94e4fd69484bbe3c00cb1973fc377f6d43cbd76666b84ed1f30d5526956ddb36`
- JarvisClient.dll: `427e2ec3e16f226651bccb4dadb7fdd2633856699eb53b36acecb93cfc610c08`
- manual-bypass.trx: `144535b30a90cd7873cceb5c4ced22570a167ccfc2a9d83ab5cd23ac42bbee12`

No unfiltered Release run, production source edit, live send, deployment, restart, migration, or activation.
