# WQ11 private host bootstrap: evidence-bound NO-GO (2026-11-16)

## Scope

This is a DEFAULT-OFF, non-deployable host capability gate on top of the
independently accepted offline `WorkQueueNativeCredentialDispatchAdapter`
(commit `a161b03b066c7356a0bbe08b3a9371a647d5059f`, integrated in
`3efdd2e1d5d3064dbefff4a8e3f17605a48b77d0`). It neither enables
Work production dispatch nor creates a provider session. Its only public
production answer is typed `PrivateHostGate.HostBridgeUnavailable`.
The pre-existing adapter continues to return its original compatibility case
`NativeCredentialAdmission.HostBridgeUnavailable` and never mints a
credential on its production entrypoint.

### Inspected boundaries

| Evidence | What it proves | What it does **not** prove |
|---|---|---|
| `browser-extension/manifest.json` and `browser-extension/background.js` (`executeJs`, `chrome.userScripts.execute`, `world: "USER_SCRIPT"`) | Operator-approved local loopback pairing, registered window/tab, page-facing user script execution | No provider-owned private invocation, no cryptographically/provider-authenticated ChatGPT execution identity |
| `Client/Modules/BrowserBridge.fs` (`Service.Execute`, `argsJson`, `executeJs`) | Tab-bound execute request and checked generation/connection; arguments are forwarded to extension | Not a hidden provider bootstrap or a private bearer-delivery channel; script JSON is not acceptable for Work credentials |
| `browser-scripts/chatgpt/launchAgent.js` | One UI submit attempt and conversation-route observations | Prompt insertion is model-visible; URL, DOM result, page script and registration are not a secret/authenticated Work session |
| `Server/McpTools.fs` and `Client/SignalR/Client.fs` | User OAuth/transport command relay with `agentId` labels | No provider-attested per-execution private callback or credential-authenticated Work first-connect route |
| `Client/Modules/WorkQueueNativeCredentialDispatchAdapter.fs` | Disposable fake provider, volatile one-shot `NativePrivateBootstrap`, original WQ03 marker, four-path WQ02 CAS and `executeCredentialFixtureAt` first connect | The caller-provided execution string and fake callback are **not** remotely attested; the implementation explicitly refuses production |

### Exact missing interfaces

`WorkQueueNativePrivateHostBridge.inspectProduction true` reports:

1. `ProviderPrivateExecutionBootstrap`: provider-supported private delivery to
   the newly created execution, with no raw credential in user/model prompt,
   browser arguments, URL, logs, Git or console.
2. `AuthenticatedProviderExecutionBinding`: authenticated host/provider proof
   tying the callback to this exact launched execution, not a tab ID, arbitrary
   session string, returned conversation URL or self-declared Agent label.
3. `CredentialAuthenticatedWorkFirstConnect`: an authenticated Jarvis
   first-connect relay consuming the original WQ02 SHA-256-verified credential,
   with original launch/claim-generation binding and replay/STOP enforcement.

Owning interfaces: provider/host private execution API and authenticated
callback, then Jarvis's Work-specific authenticated relay. None is exposed
by the checked-in host. **No switch, caller boolean, fake callback or operator
agent label may convert this into a production GO.**

The browser launcher now rejects unsupported arguments before inspecting or
clicking the composer. Its allowlist retains only `prompt`, `timeoutMs` and
the historically ignored `confirmedSubmittedPrompts` hint; the latter is
neither authentication nor prior-send proof. This is defense in depth:
**a credential must never be passed in `argsJson` in the first place**.

### Test evidence and invariant ownership

- `WorkQueueNativePrivateHostBridgeTests`: independent local two-clone
  project/account disposable Git authorities. Assert production probe with
  feature disabled/enabled does not change either Git head, mint a credential,
  create a launch, or reserve an account token.
- Offline fake-only positive: the original four-path WQ02 verifier CAS,
  one matching in-memory bootstrap consumption, wrong execution/credential
  negatives, accepted WQ03 first connect, identical UUID replay, STOP denial
  and unchanged historical `agents` paths.
- Offline fake-only uncertain send: original WQ03 journal `Unknown`, WQ02
  `OutcomeUnknown`, no second provider invocation, retained account
  reservation. It is not safe to replace a lost ACK with a new credential.
- `node --test browser-extension/tests/native-private-host-gate.test.cjs
  browser-extension/tests/chatgpt-launcher.test.cjs`: private-field
  rejection before send, no diagnostic echo, existing launcher behavior.

Actual operational proof requires a real provider-supported channel and a
new frozen independent review. The offline fake does not satisfy it.

### GO / NO-GO

| Boundary | Current state |
|---|---|
| WQ02 original verifier and four-path admission; disposable fake | Offline test evidence only |
| WQ03 one-shot original journal, unknown-send/lost ACK; disposable fake | Offline test evidence only; never blindly retry |
| WQ11 Work-native fake first connect, STOP and replay | Offline test evidence only |
| Host-private provider credential delivery/attestation | **NO-GO: missing provider API** |
| Credentialed first-connect through authenticated live relay | **NO-GO: missing Jarvis Work route** |
| Production Work launch, Scheduler, Manager, migration, public route | **NO-GO** |

Operator repair: preserve the original WQ03 journal entry, project verifier
and held account reservation after any uncertain attempt. First establish
the two missing host/provider interfaces and the Jarvis authenticated relay;
then prove provider-derived session identity, cross-execution isolation,
two-clone stale-head behavior, successful first connect and lost-ACK recovery
without replaying an original. Do not restart, deploy, schedule, send to a
provider, or cut over based on this fixture.
