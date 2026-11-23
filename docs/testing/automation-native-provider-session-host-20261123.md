# Native provider session host — Stage A source-only evidence

Task: `automation-native-provider-session-host-20261123`, specification v3
(`14e476a9bbb62b7b211be2620b58e02bc1ddf82f`). This is a **partial
implementation**, not a production host, terminal issuer, integration decision,
or automatic release authorization.

## Exact local prerequisite composition

The worktree `automation-native-provider-session-host-worker16-20261123`
was created at published property aggregate
`d63c49dacda93e246c3b38fe3854ea7a0b2f2135` (publication receipt 12704).
Exact native account `5607baf9913f4976add13c984e0182906d4fe8c7`,
Phase2 snapshot `3ea482c5fedd95b3a88817f0b8a432e93955fd34`, and Stage A
property fence `a2e244bf747c4dec89907dd6207bf3b4c4098888` were verified
as ancestors. Remote main subsequently moved to the separate Health fixture
merge `d9bea11986bd8b9ac8318c599c934c78de77c235`; no unrelated work
was imported.

The following exact accepted source heads were verified against their frozen
remote refs and composed locally using their individual patches with
`git cherry-pick -x`, not a merge of unrelated private preflight ancestry:

- W12 trust contract: `ed1b6d671067de2c27fee82852c9e63a8991105a`,
  decision 11864.
- W16 Started ingress: `12f21309c084b8a325892aab3a3c03b6aef6ce9c`,
  decision 12443.
- W17 default-off challenge wake: `314c328a3f92afc66771276e0424538d9afd564e`,
  decision 12196.
- W23 corrected canonical template: `0ff5ad90fcb219773cce0537f60333d50c3a00ab`,
  decision 12732.
- W19 browser attribution: `415ef34cf6717be2d0aad00c3dc5b3a224ed6828`.

Local checkpoint before adding B1:
`953a07d0995c7d122029622184787fe15d1e894f`, tree
`5231698b500762e49128cfe54532a5bea053922d`.
Both W12 and W16 fixture builds initially reported FS0225 missing
`Client/Modules/NativeProjectEffectFence.fs`: the published a2e244bf
Stage A JS property model does not contain that F# source. The separately
accepted W22 B1 head `74fcda395857669c5db0f80ef997b4f2d6e37fcd`
(review `c82ba0618d8b41f395b12ef1adab22b1`, decision 10938) was
verified against its immutable review ref and canonical Accepted record;
its exact three-patch series was cherry-picked, including
`5cf8f99542a9653a013683e32e56a2c32a706164` and
`67e2ed9d80607daa3dc87157d535717454e817d2`.
The F# file matches blob `255e9c6956e2e110c738e4c27e01959ea0ca6b8a`
of the W12/W16 prerequisite preflight.

Final local pre-implementation composition:
`81365fa427fb3f9d66921c84d516f04ea197207e`, tree
`877bd0be3b66ef9ce2241dca9bdbc49fef0de153`.
This is not the Integrator's independently accepted shared composition.
None of the source review refs were modified or published by this worker.

## Implemented boundary

The new module `Client/AutomationNativeProviderSessionHost.fs` is compiled
only in the standalone focused test assembly. It reads the existing real
HMAC-paired BrowserBridge window registry and rejects missing, unavailable,
ambiguous and changed generations. It does not accept an injected
browser-shaped observation as a capability. The window registry is transport
preflight evidence, not a provider first-connect.

The host rereads the signed disposable account reservation and canonical
project head; invokes the common `renderLaunchPromptAt` on exactly that
snapshot; checks the full serialized browser argument envelope with
`BrowserArguments.fits`; then adds original launch metadata and exact raw
wire JSON to the same W16 Started pre-send project CAS. The metadata includes
the original reservation/claim/dispatch/effect and source identities, account
generation/key, project name, template version/blob/prompt digest, argument
digest and observed window generation. Raw wire bytes are a separate
immutable file so a 64 KiB valid envelope is not rejected by escaping it
into a second JSON string larger than the ledger entity limit.
Both paths are derived from the original effect UUID and require absence.
The new operation kind binds hashes of both files. The existing Started
project fence still verifies signed roster/account head and original receipt,
current Started identity, source generation and lease, and project head
inside the transaction. No second launch path exists.

Reconciliation reads the original canonical operation and its immutable
origin, requires both bound paths, validates the original claim, project
binding and exact digests of both blobs and verifies the original rendered
prompt digest. It does not rerender an uncertain launch, and returns no
send/release capability. The signed-disposable function is internal and its
project name input is fixture-supplied; a future runtime adapter must derive
the canonical project display name from the authenticated runtime binding.
Reviewer candidates are denied at this Worker ingress rather than
reinterpreted as Started workers.

The only modification to the separately accepted W16 module is the
`claimVerifiedDisposableCore` private refactor and the internal
`claimVerifiedDisposableWithLaunchIntent` adapter. Existing
`claimVerifiedDisposable` uses the same operation kind, request, and
writes as before. The two extra paths are only allowed at the exact
original-effect-derived paths. The frozen W16 and W12 refs are unchanged.
No Stage B shared source files were edited; the old trusted contract is
not promoted from shapes into authority.

## Unavailable authority / explicit HOLD

Existing `BrowserBridge.Service` authenticates extension transport using
a HMAC challenge. `ListWindows()` flattens connections' registries
without exposing the owning connection; `ListTabs(ownerConnectionId)`,
`OpenTab`, and `Execute` can select or pin transport connection and
tab registration/generation. None proves an authenticated provider
session/turn identity or a provider terminal. `launchAgent.js` observes
submission, route and generating UI controls; a DOM observation, browser
ACK, disappearing tab, missing activity, or Jarvis lifecycle transition
is not independent terminal evidence. The native account read/reserve
production boundary is still explicitly disabled. The existing W12
interface is not an issuer.

Accordingly the module explicitly denies provider dispatch and terminal
release. It does not create a provider signer, fake authenticated session,
actual account reservation, live browser effect, or automatic release.
A lost or uncertain response remains occupied. The existing canonical
terminal release path must not be supplied with invented origin/provider
OIDs. Reviewer claim/source lease authority must be independently verified.

Lead challenge `6f79f22487ca45b69425b608b90fdcb3` condition
`200ca25b373aee1d6700a865a7112e8b` requests exact accepted composition,
bounded Stage B function lease, and source-real authority decision.
If the signal is unavailable, the original project fence's permitted
attributable Operator reconciliation is a possible fallback, not a
terminal substitute or automatic release. Such a path would require
separate authorized implementation of an authenticated, explicitly
attributed Operator decision bound to the same original account/project
ledger identities, reservation, dispatch, effect, source/assignment
generation, current heads, immutable prior effect record, and a unique
reconciliation operation UUID. Preserve compare-and-swap and one-time
release constraints and keep unproven evidence occupied. No such fallback
is implemented or authorized by this Stage A code.

## Tests

All test executions are focused disposable fixtures. The offline WebSocket
peer uses the actual existing BrowserBridge challenge/response and window
registry; it is not a real provider and never executes a launch. Git
fixtures use local bare repositories, two separately cloned clients, and
the signed-disposable authority already accepted in the W16 suite.
No live provider requests or production ledger mutations occur.

```sh
dotnet test tests/AutomationProviderHostTrust20261122/AutomationProviderHostTrust20261122.fsproj -c Release --no-restore --nologo --filter FullyQualifiedName!~Production
dotnet test tests/AutomationNativeAccountProjectIngress20261123/AutomationNativeAccountProjectIngress20261123.fsproj -c Release --no-restore --nologo
dotnet test tests/AutomationNativeProviderSessionHost20261123/AutomationNativeProviderSessionHost20261123.fsproj -c Release --no-restore --nologo
```

W12 focused baseline: 29 passed, deliberate production RED excluded
(job `b741e271b1b24f46b3d110d60af75af2`).
W16 baseline: 10 passed (job `254bfc12d2c34d8799e1a3885102a102`,
prior to separating wire bytes from metadata).
Final tests can reuse the exact Release Client build with
`-p:BuildProjectReferences=false`. Initial host test exposed the common
renderer prompt hash's `sha256:` prefix; reconciliation now uses that
same digest function, rather than an unprefixed account hash. The first
seven host tests passed after the correction and durable raw wire split
(job `622e011e77064e1c914967053cc93578`).

No production RED result is claimed as PASS. Stage B, an actual
source-real provider identity/terminal producer, independently accepted
Integrator composition, and fresh independent review remain outstanding.

Final host suite: **10/10 passed** (job `c89d4080cf3347218b44a7f079eda5d9`), including exact 65,536-byte accepted full envelope, minimal-only-fit rejected envelope, duplicate authenticated window registration, rewritten argument blob denial, and actual race of two separately cloned Git clients. Final W16 re-regression after atomic split: **10/10 passed** (job `3f0daa3d83ec47418ee2a1a1d5dac891`). Both final commands used `-p:BuildProjectReferences=false` with the exact previously built Release Client dependencies. All three focused suites: 49 passed; no unfiltered client tests, live provider sends, or runtime activation.
